using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using Assembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MornLib
{
    /// <summary>
    /// パッケージ内の asmdef 単位で使用状況を解析し、未使用アセンブリを可視化するタブ。
    /// Embedded 化した場合にどの asmdef を削除できるかの判断材料を提供する。
    /// </summary>
    public sealed class MornOptimizerAsmdefTab : MornOptimizerTabBase
    {
        public MornOptimizerAsmdefTab(EditorWindow owner) : base(owner)
        {
        }

        public override string TabName => "Asmdef詳細";

        private List<PackageAsmdefGroup> _groups;
        private bool _showUsed = true;
        private bool _showUnused = true;

        // パッケージ間参照マップ (解析結果を保持)
        // allReferenceMap: 参照先 asmdef名 → 参照元 asmdef名のセット
        private Dictionary<string, HashSet<string>> _allReferenceMap;
        // 連鎖で不要になる asmdef のセット
        private HashSet<string> _cascadeOrphans;

        protected override void DrawContent()
        {
            if (GUILayout.Button("解析開始", GUILayout.Height(30)))
            {
                _groups = null;
                StartAnalysis(AnalysisCoroutine());
            }

            if (_groups == null)
            {
                EditorGUILayout.HelpBox(
                    "パッケージ内の asmdef 単位で使用状況を解析します。\n" +
                    "Embedded 化した場合に削除できるアセンブリを確認できます。\n" +
                    "未使用 asmdef をチェックして「カスタムパッケージ化して削除」で実行できます。",
                    MessageType.Info);
                return;
            }

            // サマリー
            var totalAsmdefs = _groups.Sum(g => g.Entries.Count);
            var unusedAsmdefs = _groups.Sum(g => g.Entries.Count(e => !e.IsUsed));
            var packagesWithUnused = _groups.Count(g => g.Entries.Any(e => !e.IsUsed));
            EditorGUILayout.LabelField(
                $"全 {totalAsmdefs} asmdef / 未使用 {unusedAsmdefs} asmdef ({packagesWithUnused} パッケージ)",
                EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _showUnused = EditorGUILayout.ToggleLeft($"未使用 asmdef を含むパッケージ ({packagesWithUnused})", _showUnused);
            _showUsed = EditorGUILayout.ToggleLeft("全 asmdef 使用中のパッケージ", _showUsed);
            EditorGUILayout.Space();

            // 連鎖不要 asmdef を計算
            ComputeCascadeOrphans();

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            foreach (var group in _groups)
            {
                var hasUnused = group.Entries.Any(e => !e.IsUsed);
                var hasCascade = group.Entries.Any(e => _cascadeOrphans != null && _cascadeOrphans.Contains(e.AsmdefName));
                if (hasUnused && !_showUnused && !hasCascade)
                {
                    continue;
                }

                if (!hasUnused && !hasCascade && !_showUsed)
                {
                    continue;
                }

                DrawPackageGroup(group, hasUnused);
            }

            EditorGUILayout.EndScrollView();

            // 選択された asmdef がある場合、削除ボタンを表示
            var selectedEntries = _groups
                .SelectMany(g => g.Entries.Where(e => e.Selected))
                .ToList();
            var affectedPackages = _groups
                .Where(g => g.Entries.Any(e => e.Selected))
                .ToList();

            if (selectedEntries.Count > 0)
            {
                EditorGUILayout.Space();
                var buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                if (GUILayout.Button(
                        $"選択した {selectedEntries.Count} asmdef を削除（{affectedPackages.Count} パッケージをカスタム化）",
                        buttonStyle, GUILayout.Height(30)))
                {
                    EmbedAndRemoveSelectedAsmdefs(affectedPackages);
                }

                GUI.backgroundColor = Color.white;
            }
        }

        private void DrawPackageGroup(PackageAsmdefGroup group, bool hasUnused)
        {
            var headerStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            var unusedCount = group.Entries.Count(e => !e.IsUsed);
            var cascadeCount = group.Entries.Count(e => _cascadeOrphans != null && _cascadeOrphans.Contains(e.AsmdefName));
            var selectedCount = group.Entries.Count(e => e.Selected);
            var suffix = hasUnused ? $"  — 未使用 {unusedCount}/{group.Entries.Count}" : "";
            if (cascadeCount > 0)
            {
                suffix += $"  ⚡連鎖 {cascadeCount}";
            }
            if (selectedCount > 0)
            {
                suffix += $"  [選択: {selectedCount}]";
            }

            group.Foldout = EditorGUILayout.Foldout(group.Foldout, $"{group.DisplayName} ({group.PackageName}){suffix}", true, headerStyle);

            if (!group.Foldout)
            {
                return;
            }

            EditorGUI.indentLevel++;

            // 未使用ありの場合、一括選択ボタン
            if (hasUnused)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                if (GUILayout.Button("未使用を全選択", EditorStyles.miniButtonLeft, GUILayout.Width(100)))
                {
                    foreach (var e in group.Entries)
                    {
                        if (!e.IsUsed && !e.IsEditorOnly)
                        {
                            e.Selected = true;
                        }
                    }
                }

                if (GUILayout.Button("選択解除", EditorStyles.miniButtonRight, GUILayout.Width(80)))
                {
                    foreach (var e in group.Entries)
                    {
                        e.Selected = false;
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            foreach (var entry in group.Entries)
            {
                if (entry.IsEditorOnly)
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.LabelField($"{entry.AsmdefName}  [Editor専用]");
                    EditorGUI.EndDisabledGroup();
                    continue;
                }

                var isCascade = _cascadeOrphans != null && _cascadeOrphans.Contains(entry.AsmdefName);

                if (isCascade)
                {
                    // 連鎖で不要になる asmdef → チェック可能
                    var color = GUI.color;
                    GUI.color = new Color(1f, 0.8f, 0.5f);
                    var prev = entry.Selected;
                    entry.Selected = EditorGUILayout.ToggleLeft(
                        $"⚡ {entry.AsmdefName}  — 削除で不要になる", entry.Selected);
                    if (entry.Selected != prev)
                    {
                        Repaint();
                    }

                    GUI.color = color;
                }
                else if (entry.IsUsed)
                {
                    EditorGUILayout.LabelField($"✓ {entry.AsmdefName}", entry.UsageReason);
                }
                else
                {
                    var color = GUI.color;
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    var prev = entry.Selected;
                    entry.Selected = EditorGUILayout.ToggleLeft($"✗ {entry.AsmdefName}  — 未使用", entry.Selected);
                    if (entry.Selected != prev)
                    {
                        Repaint();
                    }

                    GUI.color = color;
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }

        // ── 解析コルーチン ──

        private IEnumerator AnalysisCoroutine()
        {
            var assetsPath = Application.dataPath;
            var scriptAssembliesDir = Path.Combine(assetsPath, "..", "Library", "ScriptAssemblies");

            // Phase 1: パッケージ一覧取得
            SetProgress("パッケージ一覧を取得中...", 0f);
            yield return null;

            var lockPackages = ParsePackagesLock(Path.Combine(assetsPath, "..", "Packages", "packages-lock.json"));

            var packageInfoMap = new Dictionary<string, PackageInfo>();
            foreach (var pkgName in lockPackages.Keys)
            {
                var pi = PackageInfo.FindForPackageName(pkgName);
                if (pi != null)
                {
                    packageInfoMap[pkgName] = pi;
                }
            }

            // Phase 2: Assets/ アセンブリ + 全パッケージ DLL から参照マップ構築
            SetProgress("Assets/ アセンブリを収集中...", 0.05f);
            yield return null;

            var assetsAssemblies = CollectAssetsAssemblies(assetsPath);

            // referenceMap: 参照先 asmdef名 → 参照元 asmdef名のセット (Assets/ からの参照)
            var referenceMap = new Dictionary<string, HashSet<string>>();
            // allReferenceMap: 参照先 asmdef名 → 参照元 asmdef名のセット (全DLLからの参照、パッケージ間含む)
            var allReferenceMap = new Dictionary<string, HashSet<string>>();

            for (var i = 0; i < assetsAssemblies.Count; i++)
            {
                var asm = assetsAssemblies[i];
                SetProgress($"Assets/ DLL解析中... ({i + 1}/{assetsAssemblies.Count}) {asm.name}",
                    0.05f + 0.1f * i / assetsAssemblies.Count);

                var dllPath = Path.Combine(scriptAssembliesDir, asm.name + ".dll");
                if (!File.Exists(dllPath))
                {
                    continue;
                }

                try
                {
                    var bytes = File.ReadAllBytes(dllPath);
                    var loadedAsm = System.Reflection.Assembly.Load(bytes);
                    foreach (var refAsm in loadedAsm.GetReferencedAssemblies())
                    {
                        if (!referenceMap.TryGetValue(refAsm.Name, out var set))
                        {
                            set = new HashSet<string>();
                            referenceMap[refAsm.Name] = set;
                        }

                        set.Add(asm.name);

                        if (!allReferenceMap.TryGetValue(refAsm.Name, out var allSet))
                        {
                            allSet = new HashSet<string>();
                            allReferenceMap[refAsm.Name] = allSet;
                        }

                        allSet.Add(asm.name);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MornOptimizer] {asm.name}.dll の参照解析に失敗: {e.Message}");
                }

                yield return null;
            }

            // Phase 2.5: CompilationPipeline から全アセンブリの参照グラフを構築
            // Unity が実際のコンパイルに使う正確な参照関係を取得する
            SetProgress("全アセンブリ参照グラフを構築中...", 0.15f);
            yield return null;

            var allCompilationAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                .ToArray();

            foreach (var asm in allCompilationAssemblies)
            {
                if (asm.assemblyReferences == null)
                {
                    continue;
                }

                foreach (var refAsm in asm.assemblyReferences)
                {
                    if (!allReferenceMap.TryGetValue(refAsm.name, out var allSet))
                    {
                        allSet = new HashSet<string>();
                        allReferenceMap[refAsm.name] = allSet;
                    }

                    allSet.Add(asm.name);
                }
            }

            // Phase 3: ソースコードのトークン化 (型名検索用)
            SetProgress("ソースコードをトークン化中...", 0.28f);
            yield return null;

            var sourceWordSet = new HashSet<string>();
            foreach (var asm in assetsAssemblies)
            {
                foreach (var sourceFile in asm.sourceFiles)
                {
                    var fullPath = Path.GetFullPath(sourceFile);
                    if (!fullPath.StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(sourceFile))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(sourceFile);
                    foreach (Match m in Regex.Matches(text, @"\b[A-Z]\w+\b"))
                    {
                        sourceWordSet.Add(m.Value);
                    }
                }

                yield return null;
            }

            // Phase 4: Scene/Prefab から型名を収集
            SetProgress("Scene/Prefab をスキャン中...", 0.45f);
            yield return null;

            var assetComponentNames = new HashSet<string>();
            var assetFiles = Directory.GetFiles(assetsPath, "*.unity", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(assetsPath, "*.prefab", SearchOption.AllDirectories))
                .ToArray();

            for (var i = 0; i < assetFiles.Length; i++)
            {
                if (i % 10 == 0)
                {
                    SetProgress($"Scene/Prefab スキャン中... ({i + 1}/{assetFiles.Length})",
                        0.45f + 0.15f * i / assetFiles.Length);
                    yield return null;
                }

                var text = File.ReadAllText(assetFiles[i]);
                foreach (Match m in Regex.Matches(text, @"^(\w+):", RegexOptions.Multiline))
                {
                    assetComponentNames.Add(m.Groups[1].Value);
                }
            }

            // Phase 5: パッケージ内 asmdef を列挙し、使用判定
            var packageNames = lockPackages.Keys
                .Where(k => !k.StartsWith("com.unity.modules.") && !k.StartsWith("com.unity.feature."))
                .ToArray();

            var groups = new List<PackageAsmdefGroup>();

            for (var i = 0; i < packageNames.Length; i++)
            {
                var pkgName = packageNames[i];
                SetProgress($"asmdef 解析中... ({i + 1}/{packageNames.Length}) {pkgName}",
                    0.6f + 0.35f * i / packageNames.Length);

                var pi = packageInfoMap.GetValueOrDefault(pkgName);
                var resolvedPath = pi?.resolvedPath;
                if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
                {
                    continue;
                }

                var asmdefFiles = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories);
                if (asmdefFiles.Length <= 1)
                {
                    // asmdef が 1 つ以下のパッケージは詳細解析の意味が薄い
                    continue;
                }

                var entries = new List<AsmdefEntry>();
                foreach (var asmdefFile in asmdefFiles)
                {
                    var asmdefJson = File.ReadAllText(asmdefFile);
                    var asmdefName = ExtractAsmdefName(asmdefJson);
                    if (string.IsNullOrEmpty(asmdefName))
                    {
                        continue;
                    }

                    var isEditorOnly = IsEditorOnlyAsmdef(asmdefJson);

                    // 使用判定 1: DLL 参照
                    var dllReferenced = referenceMap.ContainsKey(asmdefName);
                    string usageReason = null;
                    if (dllReferenced)
                    {
                        var refs = referenceMap[asmdefName];
                        usageReason = string.Join(", ", refs.OrderBy(x => x));
                    }

                    // 使用判定 2: エクスポート型名がソースコードまたは Scene/Prefab に登場
                    if (!dllReferenced && !isEditorOnly)
                    {
                        var typeNames = GetExportedTypeNames(asmdefName, scriptAssembliesDir);
                        foreach (var typeName in typeNames)
                        {
                            if (sourceWordSet.Contains(typeName))
                            {
                                usageReason = $"型参照: {typeName}";
                                break;
                            }

                            if (assetComponentNames.Contains(typeName))
                            {
                                usageReason = $"コンポーネント: {typeName}";
                                break;
                            }
                        }
                    }

                    // 使用判定 3: 同パッケージ内の他の使用中 asmdef が参照している場合
                    // → Phase 6 で後処理

                    entries.Add(new AsmdefEntry
                    {
                        AsmdefName = asmdefName,
                        AsmdefPath = asmdefFile,
                        IsEditorOnly = isEditorOnly,
                        IsUsed = isEditorOnly || dllReferenced || usageReason != null,
                        UsageReason = usageReason ?? "",
                    });
                }

                if (entries.Count > 0)
                {
                    groups.Add(new PackageAsmdefGroup
                    {
                        PackageName = pkgName,
                        DisplayName = pi?.displayName ?? pkgName,
                        Entries = entries,
                        Foldout = false,
                    });
                }

                yield return null;
            }

            // Phase 6: 他パッケージからの参照を検出
            // allReferenceMap を使い、解析対象外のパッケージ DLL からの参照も使用中とマーク
            SetProgress("パッケージ間依存を解決中...", 0.93f);
            yield return null;

            // 全エントリの asmdef名 → エントリ の逆引きマップ
            var allEntryMap = new Dictionary<string, (PackageAsmdefGroup Group, AsmdefEntry Entry)>();
            foreach (var group in groups)
            {
                foreach (var entry in group.Entries)
                {
                    allEntryMap[entry.AsmdefName] = (group, entry);
                }
            }

            // 他パッケージ DLL からの参照で使用中にマーク
            foreach (var group in groups)
            {
                foreach (var entry in group.Entries)
                {
                    if (entry.IsUsed || entry.IsEditorOnly)
                    {
                        continue;
                    }

                    if (!allReferenceMap.TryGetValue(entry.AsmdefName, out var referencers))
                    {
                        continue;
                    }

                    // 参照元が同パッケージ内でない外部 DLL なら使用中
                    var samePackageAsmdefs = new HashSet<string>(group.Entries.Select(e => e.AsmdefName));
                    foreach (var refName in referencers)
                    {
                        if (samePackageAsmdefs.Contains(refName))
                        {
                            continue;
                        }

                        entry.IsUsed = true;
                        entry.UsageReason = $"← {refName} (パッケージ間参照)";
                        break;
                    }
                }
            }

            // Phase 6.5: パッケージ内 asmdef 間の依存を解決 (allReferenceMap ベース)
            // 使用中 asmdef が参照している同パッケージの asmdef も使用中とマーク
            // Editor-only asmdef も含めて伝播する (Editor が Runtime を参照するケース)
            SetProgress("パッケージ内依存を解決中...", 0.96f);
            yield return null;

            foreach (var group in groups)
            {
                var asmdefNames = new HashSet<string>(group.Entries.Select(e => e.AsmdefName));
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var entry in group.Entries)
                    {
                        if (!entry.IsUsed)
                        {
                            continue;
                        }

                        // allReferenceMap で、この asmdef が参照している同パッケージの asmdef を探す
                        foreach (var otherEntry in group.Entries)
                        {
                            if (otherEntry.IsUsed)
                            {
                                continue;
                            }

                            if (!allReferenceMap.TryGetValue(otherEntry.AsmdefName, out var referencers))
                            {
                                continue;
                            }

                            if (referencers.Contains(entry.AsmdefName))
                            {
                                otherEntry.IsUsed = true;
                                otherEntry.UsageReason = $"← {entry.AsmdefName}";
                                changed = true;
                            }
                        }
                    }
                }
            }

            // 未使用 asmdef を持つパッケージを先に表示
            groups.Sort((a, b) =>
            {
                var aHasUnused = a.Entries.Any(e => !e.IsUsed) ? 0 : 1;
                var bHasUnused = b.Entries.Any(e => !e.IsUsed) ? 0 : 1;
                if (aHasUnused != bHasUnused)
                {
                    return aHasUnused - bHasUnused;
                }

                return string.Compare(a.PackageName, b.PackageName, StringComparison.Ordinal);
            });

            // 未使用ありパッケージはデフォルトで開く
            foreach (var group in groups)
            {
                if (group.Entries.Any(e => !e.IsUsed))
                {
                    group.Foldout = true;
                }
            }

            _groups = groups;
            _allReferenceMap = allReferenceMap;
            _cascadeOrphans = new HashSet<string>();

            var totalUnused = groups.Sum(g => g.Entries.Count(e => !e.IsUsed));
            Debug.Log($"[MornOptimizer] Asmdef詳細: {groups.Count} パッケージ, {totalUnused} 未使用 asmdef 検出");

            SetProgress("完了", 1f);
        }

        // ── 連鎖不要 asmdef の計算 ──

        private void ComputeCascadeOrphans()
        {
            if (_cascadeOrphans == null)
            {
                _cascadeOrphans = new HashSet<string>();
            }

            _cascadeOrphans.Clear();

            if (_groups == null || _allReferenceMap == null)
            {
                return;
            }

            // 全エントリのマップ
            var allEntryMap = new Dictionary<string, AsmdefEntry>();
            foreach (var group in _groups)
            {
                foreach (var entry in group.Entries)
                {
                    allEntryMap[entry.AsmdefName] = entry;
                }
            }

            // ユーザーが選択した (削除予定の) asmdef 名
            var removing = new HashSet<string>();
            foreach (var group in _groups)
            {
                foreach (var entry in group.Entries)
                {
                    if (entry.Selected)
                    {
                        removing.Add(entry.AsmdefName);
                    }
                }
            }

            if (removing.Count == 0)
            {
                // 選択がなくなったら、連鎖選択も解除
                foreach (var group in _groups)
                {
                    foreach (var entry in group.Entries)
                    {
                        if (entry.IsUsed && entry.Selected)
                        {
                            entry.Selected = false;
                        }
                    }
                }

                return;
            }

            // 連鎖計算: 参照元が全て削除予定 or 連鎖不要 なら、その asmdef も不要
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var group in _groups)
                {
                    foreach (var entry in group.Entries)
                    {
                        if (entry.IsEditorOnly || _cascadeOrphans.Contains(entry.AsmdefName))
                        {
                            continue;
                        }

                        // 既に未使用で選択済みのものはスキップ
                        if (!entry.IsUsed && entry.Selected)
                        {
                            continue;
                        }

                        // この asmdef を参照している全 DLL を確認
                        if (!_allReferenceMap.TryGetValue(entry.AsmdefName, out var referencers))
                        {
                            continue;
                        }

                        // 参照元が全て「削除予定」か「連鎖不要」なら、この asmdef も不要
                        var allReferencersRemoving = referencers.All(
                            r => removing.Contains(r) || _cascadeOrphans.Contains(r));

                        if (allReferencersRemoving)
                        {
                            _cascadeOrphans.Add(entry.AsmdefName);
                            changed = true;
                        }
                    }
                }
            }

            // 連鎖対象から外れた使用中 asmdef の選択を解除
            foreach (var group in _groups)
            {
                foreach (var entry in group.Entries)
                {
                    if (entry.IsUsed && entry.Selected && !_cascadeOrphans.Contains(entry.AsmdefName))
                    {
                        entry.Selected = false;
                    }
                }
            }
        }

        // ── Embedded化 + asmdef削除 ──

        private void EmbedAndRemoveSelectedAsmdefs(List<PackageAsmdefGroup> affectedGroups)
        {
            // 確認メッセージ作成
            var msgLines = new List<string>();
            foreach (var group in affectedGroups)
            {
                var selected = group.Entries.Where(e => e.Selected).ToList();
                msgLines.Add($"■ {group.DisplayName} ({group.PackageName})");
                msgLines.Add("  → CustomPackages/ にカスタムパッケージ化");
                foreach (var entry in selected)
                {
                    msgLines.Add($"  削除: {entry.AsmdefName}");
                }

                msgLines.Add("");
            }

            if (!EditorUtility.DisplayDialog(
                    "⚠ カスタムパッケージ化 + asmdef削除",
                    $"以下の操作を実行します:\n\n{string.Join("\n", msgLines)}" +
                    "パッケージを CustomPackages/ にコピーし、選択した asmdef のディレクトリを削除します。\n" +
                    "manifest.json は file:../CustomPackages/xxx に更新されます。\n\n" +
                    "【警告】この操作は元に戻せません。\n" +
                    "実行前に必ず git の差分を確認し、コミットまたはローカルバックアップを作成してください。\n" +
                    "この操作によって発生した問題について、ツール側では一切の責任を負いません。\n\n" +
                    "続行しますか？",
                    "理解した上で実行する",
                    "キャンセル"))
            {
                return;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var customPackagesDir = Path.Combine(projectRoot, "CustomPackages");
            var packagesDir = Path.Combine(projectRoot, "Packages");
            var manifestPath = Path.Combine(packagesDir, "manifest.json");

            Directory.CreateDirectory(customPackagesDir);

            // Phase 1: 全パッケージを CustomPackages/ にコピー + asmdef削除
            // CustomPackages/ は Unity のパッケージマネージャーが監視しないため、安全にコピーできる
            var processedGroups = new List<PackageAsmdefGroup>();

            foreach (var group in affectedGroups)
            {
                var pi = PackageInfo.FindForPackageName(group.PackageName);
                if (pi == null || string.IsNullOrEmpty(pi.resolvedPath))
                {
                    Debug.LogError($"[MornOptimizer] {group.PackageName} が見つかりません。スキップします。");
                    continue;
                }

                var sourcePath = Path.GetFullPath(pi.resolvedPath);
                var customPath = Path.Combine(customPackagesDir, group.PackageName);

                // 既に CustomPackages/ にある場合はコピー不要
                var alreadyCustom = sourcePath.StartsWith(Path.GetFullPath(customPackagesDir), StringComparison.OrdinalIgnoreCase);

                if (!alreadyCustom)
                {
                    // package.json の存在確認
                    if (!File.Exists(Path.Combine(sourcePath, "package.json")))
                    {
                        Debug.LogError($"[MornOptimizer] {group.PackageName} のソースに package.json がありません: {sourcePath}");
                        continue;
                    }

                    // CustomPackages/ にコピー
                    if (Directory.Exists(customPath))
                    {
                        Directory.Delete(customPath, true);
                    }

                    try
                    {
                        CopyDirectory(sourcePath, customPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[MornOptimizer] {group.PackageName} のコピーに失敗: {e.Message}");
                        if (Directory.Exists(customPath))
                        {
                            try { Directory.Delete(customPath, true); } catch { /* ignore */ }
                        }

                        continue;
                    }

                    // コピー後の検証
                    if (!File.Exists(Path.Combine(customPath, "package.json")))
                    {
                        Debug.LogError($"[MornOptimizer] {group.PackageName} のコピーに package.json が含まれていません。");
                        if (Directory.Exists(customPath))
                        {
                            try { Directory.Delete(customPath, true); } catch { /* ignore */ }
                        }

                        continue;
                    }

                    Debug.Log($"[MornOptimizer] {group.PackageName} を CustomPackages/ にコピーしました。");
                }

                // 選択された asmdef のディレクトリを削除
                var selected = group.Entries.Where(e => e.Selected).ToList();
                foreach (var entry in selected)
                {
                    var relativePath = GetRelativePath(sourcePath, entry.AsmdefPath);
                    var targetAsmdefPath = Path.Combine(customPath, relativePath);
                    var targetDir = Path.GetDirectoryName(targetAsmdefPath);

                    if (targetDir == null || !Directory.Exists(targetDir))
                    {
                        Debug.LogWarning($"[MornOptimizer] {entry.AsmdefName} のディレクトリが見つかりません: {targetDir}");
                        continue;
                    }

                    if (string.Equals(Path.GetFullPath(targetDir), Path.GetFullPath(customPath), StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(targetAsmdefPath))
                        {
                            File.Delete(targetAsmdefPath);
                            Debug.Log($"[MornOptimizer] asmdef を削除: {entry.AsmdefName}");
                        }
                    }
                    else
                    {
                        try
                        {
                            Directory.Delete(targetDir, true);
                            Debug.Log($"[MornOptimizer] ディレクトリを削除: {entry.AsmdefName}");
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[MornOptimizer] {entry.AsmdefName} のディレクトリ削除に失敗: {e.Message}");
                        }
                    }
                }

                // package.json の dependencies をクリーンアップ
                // 残っている asmdef が実際に参照しているパッケージだけを残す
                CleanupPackageJsonDependencies(customPath);

                processedGroups.Add(group);
            }

            // Phase 2: manifest.json を一括更新
            if (processedGroups.Count > 0)
            {
                var manifestText = File.ReadAllText(manifestPath);
                var manifestLines = manifestText.Split('\n').ToList();

                foreach (var group in processedGroups)
                {
                    UpdateManifestToCustomPath(manifestLines, group.PackageName);
                }

                FixTrailingCommas(manifestLines);
                File.WriteAllText(manifestPath, string.Join("\n", manifestLines));
            }

            Debug.Log($"[MornOptimizer] {processedGroups.Count} パッケージを処理しました。パッケージを再解決します。");
            Client.Resolve();
            _groups = null;
        }

        /// <summary>
        /// カスタムパッケージの package.json から、残っている asmdef が参照していない依存を除去する。
        /// </summary>
        private void CleanupPackageJsonDependencies(string packageDir)
        {
            var packageJsonPath = Path.Combine(packageDir, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return;
            }

            // 残っている asmdef が実際に参照しているアセンブリ名・DLL名を収集
            var remainingAsmdefFiles = Directory.GetFiles(packageDir, "*.asmdef", SearchOption.AllDirectories);
            var referencedAsmNames = new HashSet<string>();
            var referencedDllNames = new HashSet<string>();
            foreach (var asmdefFile in remainingAsmdefFiles)
            {
                var json = File.ReadAllText(asmdefFile);

                // references フィールド (asmdef 参照)
                var refsIdx = json.IndexOf("\"references\"", StringComparison.Ordinal);
                if (refsIdx >= 0)
                {
                    var bracketStart = json.IndexOf('[', refsIdx);
                    var bracketEnd = json.IndexOf(']', bracketStart);
                    if (bracketStart >= 0 && bracketEnd >= 0)
                    {
                        var content = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                        foreach (Match m in Regex.Matches(content, "\"([^\"]+)\""))
                        {
                            referencedAsmNames.Add(m.Groups[1].Value);
                        }
                    }
                }

                // precompiledReferences フィールド (DLL 参照: nunit.framework.dll 等)
                var preIdx = json.IndexOf("\"precompiledReferences\"", StringComparison.Ordinal);
                if (preIdx >= 0)
                {
                    var bracketStart = json.IndexOf('[', preIdx);
                    var bracketEnd = json.IndexOf(']', bracketStart);
                    if (bracketStart >= 0 && bracketEnd >= 0)
                    {
                        var content = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                        foreach (Match m in Regex.Matches(content, "\"([^\"]+)\""))
                        {
                            referencedDllNames.Add(m.Groups[1].Value);
                        }
                    }
                }
            }

            // 参照されているアセンブリ名/GUID → パッケージ名を解決
            // CompilationPipeline は asmdef 削除直後にキャッシュが古い場合があるため使わない
            var resolvedReferencedPackages = new HashSet<string>();

            // 全パッケージの asmdef 名・GUID・DLL をマッピング
            var asmNameToPackageName = new Dictionary<string, string>();
            var guidToPackageName = new Dictionary<string, string>();
            var dllNameToPackageName = new Dictionary<string, string>();

            foreach (var pi in PackageInfo.GetAllRegisteredPackages())
            {
                if (string.IsNullOrEmpty(pi.resolvedPath) || !Directory.Exists(pi.resolvedPath))
                {
                    continue;
                }

                foreach (var asmdefFile in Directory.GetFiles(pi.resolvedPath, "*.asmdef", SearchOption.AllDirectories))
                {
                    var name = ExtractAsmdefName(File.ReadAllText(asmdefFile));
                    if (!string.IsNullOrEmpty(name))
                    {
                        asmNameToPackageName[name] = pi.name;
                    }

                    var metaPath = asmdefFile + ".meta";
                    if (File.Exists(metaPath))
                    {
                        var metaText = File.ReadAllText(metaPath);
                        var guidMatch = Regex.Match(metaText, @"guid:\s*([0-9a-f]+)");
                        if (guidMatch.Success)
                        {
                            guidToPackageName[guidMatch.Groups[1].Value] = pi.name;
                        }
                    }
                }

                foreach (var dll in Directory.GetFiles(pi.resolvedPath, "*.dll", SearchOption.AllDirectories))
                {
                    dllNameToPackageName[Path.GetFileName(dll)] = pi.name;
                }
            }

            // references を解決
            foreach (var refName in referencedAsmNames)
            {
                if (refName.StartsWith("GUID:"))
                {
                    var guid = refName.Substring(5);
                    if (guidToPackageName.TryGetValue(guid, out var pkgName))
                    {
                        resolvedReferencedPackages.Add(pkgName);
                    }
                }
                else
                {
                    if (asmNameToPackageName.TryGetValue(refName, out var pkgName))
                    {
                        resolvedReferencedPackages.Add(pkgName);
                    }
                }
            }

            // precompiledReferences を解決
            foreach (var dllName in referencedDllNames)
            {
                if (dllNameToPackageName.TryGetValue(dllName, out var pkgName))
                {
                    resolvedReferencedPackages.Add(pkgName);
                }
            }

            // package.json を読み込んで不要な dependencies を除去
            var packageJson = File.ReadAllText(packageJsonPath);
            var depsIdx = packageJson.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (depsIdx < 0)
            {
                return;
            }

            var braceStart = packageJson.IndexOf('{', depsIdx);
            var braceEnd = packageJson.IndexOf('}', braceStart);
            if (braceStart < 0 || braceEnd < 0)
            {
                return;
            }

            var depsBlock = packageJson.Substring(braceStart + 1, braceEnd - braceStart - 1);
            var lines = depsBlock.Split('\n').ToList();
            var removedDeps = new List<string>();

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("\""))
                {
                    continue;
                }

                var endQuote = trimmed.IndexOf('"', 1);
                if (endQuote <= 1)
                {
                    continue;
                }

                var depName = trimmed.Substring(1, endQuote - 1);

                // この依存先パッケージが、残っている asmdef から参照されていなければ除去
                if (!resolvedReferencedPackages.Contains(depName))
                {
                    removedDeps.Add(depName);
                    lines.RemoveAt(i);
                }
            }

            if (removedDeps.Count == 0)
            {
                return;
            }

            // 末尾カンマ修正
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("\""))
                {
                    lines[i] = lines[i].TrimEnd();
                    if (lines[i].EndsWith(","))
                    {
                        lines[i] = lines[i][..^1];
                    }

                    break;
                }
            }

            var newDepsBlock = string.Join("\n", lines);
            var newPackageJson = packageJson.Substring(0, braceStart + 1) + newDepsBlock + packageJson.Substring(braceEnd);
            File.WriteAllText(packageJsonPath, newPackageJson);

            Debug.Log($"[MornOptimizer] {Path.GetFileName(Path.GetDirectoryName(packageJsonPath))} の package.json から {removedDeps.Count} 依存を除去: {string.Join(", ", removedDeps)}");
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                CopyFileWithRetry(file, destFile);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                // .git 等の隠しディレクトリはスキップ
                if (dirName.StartsWith("."))
                {
                    continue;
                }

                CopyDirectory(dir, Path.Combine(destDir, dirName));
            }
        }

        private static void CopyFileWithRetry(string source, string dest)
        {
            // Unity が PackageCache 内のファイルをロックしているため、
            // 最初から共有読み取りモードでコピーする
            using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
            sourceStream.CopyTo(destStream);
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            var baseUri = new Uri(Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        private static void UpdateManifestToCustomPath(List<string> lines, string packageName)
        {
            // manifest.json の参照を file:../CustomPackages/xxx に更新
            var fileRef = $"file:../CustomPackages/{packageName}";
            UpdateManifestReference(lines, packageName, fileRef);
        }

        private static void UpdateManifestToFileReference(List<string> lines, string packageName)
        {
            UpdateManifestReference(lines, packageName, $"file:{packageName}");
        }

        private static void UpdateManifestReference(List<string> lines, string packageName, string newReference)
        {
            var inDependencies = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Contains("\"dependencies\""))
                {
                    inDependencies = true;
                    continue;
                }

                if (inDependencies && trimmed.StartsWith("}"))
                {
                    break;
                }

                if (!inDependencies)
                {
                    continue;
                }

                if (!trimmed.Contains($"\"{packageName}\""))
                {
                    continue;
                }

                var colonIdx = lines[i].IndexOf(':');
                if (colonIdx < 0)
                {
                    continue;
                }

                var prefix = lines[i].Substring(0, colonIdx + 1);
                var hasComma = trimmed.EndsWith(",");
                lines[i] = $"{prefix} \"{newReference}\"{(hasComma ? "," : "")}";
                return;
            }

            // manifest に存在しない場合は dependencies ブロックの末尾に追加
            inDependencies = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Contains("\"dependencies\""))
                {
                    inDependencies = true;
                    continue;
                }

                if (inDependencies && trimmed.StartsWith("}"))
                {
                    for (var j = i - 1; j >= 0; j--)
                    {
                        var prevTrimmed = lines[j].Trim();
                        if (prevTrimmed.StartsWith("\""))
                        {
                            if (!lines[j].TrimEnd().EndsWith(","))
                            {
                                lines[j] = lines[j].TrimEnd() + ",";
                            }

                            break;
                        }
                    }

                    lines.Insert(i, $"    \"{packageName}\": \"{newReference}\"");
                    return;
                }
            }
        }

        private static void FixTrailingCommas(List<string> lines)
        {
            var inDependencies = false;
            var lastEntryIndex = -1;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Contains("\"dependencies\""))
                {
                    inDependencies = true;
                    continue;
                }

                if (inDependencies && trimmed.StartsWith("}"))
                {
                    if (lastEntryIndex >= 0)
                    {
                        lines[lastEntryIndex] = lines[lastEntryIndex].TrimEnd();
                        if (lines[lastEntryIndex].EndsWith(","))
                        {
                            lines[lastEntryIndex] = lines[lastEntryIndex][..^1];
                        }
                    }

                    break;
                }

                if (inDependencies && trimmed.StartsWith("\""))
                {
                    lastEntryIndex = i;
                }
            }
        }

        // ── ユーティリティ ──

        private List<Assembly> CollectAssetsAssemblies(string assetsPath)
        {
            var result = new List<Assembly>();
            var allAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Editor));

            foreach (var asm in allAssemblies)
            {
                if (asm.sourceFiles == null || asm.sourceFiles.Length == 0)
                {
                    continue;
                }

                foreach (var sourceFile in asm.sourceFiles)
                {
                    var fullPath = Path.GetFullPath(sourceFile);
                    if (fullPath.StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(asm);
                        break;
                    }
                }
            }

            return result;
        }

        private HashSet<string> GetExportedTypeNames(string asmdefName, string scriptAssembliesDir)
        {
            var typeNames = new HashSet<string>();
            var dllPath = Path.Combine(scriptAssembliesDir, asmdefName + ".dll");
            if (!File.Exists(dllPath))
            {
                return typeNames;
            }

            try
            {
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                var loaded = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == asmdefName);
                if (loaded == null)
                {
                    loaded = System.Reflection.Assembly.Load(File.ReadAllBytes(dllPath));
                }

                foreach (var type in loaded.GetExportedTypes())
                {
                    if (!type.IsNested && !type.Name.StartsWith("<"))
                    {
                        typeNames.Add(type.Name);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return typeNames;
        }

        private string ExtractAsmdefName(string json)
        {
            var nameKey = "\"name\"";
            var idx = json.IndexOf(nameKey, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            var colonIdx = json.IndexOf(':', idx + nameKey.Length);
            if (colonIdx < 0)
            {
                return null;
            }

            var firstQuote = json.IndexOf('"', colonIdx + 1);
            if (firstQuote < 0)
            {
                return null;
            }

            var secondQuote = json.IndexOf('"', firstQuote + 1);
            return secondQuote < 0 ? null : json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }


        private bool IsEditorOnlyAsmdef(string json)
        {
            var idx = json.IndexOf("\"includePlatforms\"", StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            var bracketStart = json.IndexOf('[', idx);
            var bracketEnd = json.IndexOf(']', bracketStart);
            if (bracketStart < 0 || bracketEnd < 0)
            {
                return false;
            }

            var content = json.Substring(bracketStart, bracketEnd - bracketStart + 1);
            return content.Contains("\"Editor\"");
        }

        private Dictionary<string, LockPackageData> ParsePackagesLock(string lockPath)
        {
            var result = new Dictionary<string, LockPackageData>();
            if (!File.Exists(lockPath))
            {
                return result;
            }

            var json = File.ReadAllText(lockPath);
            var depsStart = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (depsStart < 0)
            {
                return result;
            }

            var braceStart = json.IndexOf('{', depsStart);
            if (braceStart < 0)
            {
                return result;
            }

            var depth = 0;
            var i = braceStart;
            var entryStart = -1;
            var currentName = "";

            while (i < json.Length)
            {
                var c = json[i];
                if (c == '"' && depth == 1 && entryStart < 0)
                {
                    var nameEnd = json.IndexOf('"', i + 1);
                    currentName = json.Substring(i + 1, nameEnd - i - 1);
                    i = nameEnd + 1;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    if (depth == 2)
                    {
                        entryStart = i;
                    }
                }
                else if (c == '}')
                {
                    if (depth == 2 && entryStart >= 0)
                    {
                        var entryJson = json.Substring(entryStart, i - entryStart + 1);
                        result[currentName] = ParseLockEntry(entryJson);
                        entryStart = -1;
                    }

                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                i++;
            }

            return result;
        }

        private LockPackageData ParseLockEntry(string entryJson)
        {
            var data = new LockPackageData();
            var sIdx = entryJson.IndexOf("\"source\"", StringComparison.Ordinal);
            if (sIdx >= 0)
            {
                var q1 = entryJson.IndexOf('"', entryJson.IndexOf(':', sIdx) + 1);
                var q2 = entryJson.IndexOf('"', q1 + 1);
                if (q1 >= 0 && q2 > q1)
                {
                    data.Source = entryJson.Substring(q1 + 1, q2 - q1 - 1);
                }
            }

            return data;
        }

        // ── データクラス ──

        private class LockPackageData
        {
            public string Source = "";
        }

        private class PackageAsmdefGroup
        {
            public string PackageName;
            public string DisplayName;
            public List<AsmdefEntry> Entries;
            public bool Foldout;
        }

        private class AsmdefEntry
        {
            public string AsmdefName;
            public string AsmdefPath;
            public bool IsEditorOnly;
            public bool IsUsed;
            public string UsageReason;
            public bool Selected;
        }
    }
}
