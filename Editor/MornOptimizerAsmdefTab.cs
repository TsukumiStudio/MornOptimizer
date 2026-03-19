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
                    "Embedded 化した場合に削除できるアセンブリを確認できます。",
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

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            foreach (var group in _groups)
            {
                var hasUnused = group.Entries.Any(e => !e.IsUsed);
                if (hasUnused && !_showUnused)
                {
                    continue;
                }

                if (!hasUnused && !_showUsed)
                {
                    continue;
                }

                DrawPackageGroup(group, hasUnused);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPackageGroup(PackageAsmdefGroup group, bool hasUnused)
        {
            var headerStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            var unusedCount = group.Entries.Count(e => !e.IsUsed);
            var suffix = hasUnused ? $"  — 未使用 {unusedCount}/{group.Entries.Count}" : "";
            group.Foldout = EditorGUILayout.Foldout(group.Foldout, $"{group.DisplayName} ({group.PackageName}){suffix}", true, headerStyle);

            if (!group.Foldout)
            {
                return;
            }

            EditorGUI.indentLevel++;

            foreach (var entry in group.Entries)
            {
                if (entry.IsEditorOnly)
                {
                    // Editor専用は薄く表示
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.LabelField($"{entry.AsmdefName}  [Editor専用]");
                    EditorGUI.EndDisabledGroup();
                    continue;
                }

                if (entry.IsUsed)
                {
                    EditorGUILayout.LabelField($"✓ {entry.AsmdefName}", entry.UsageReason);
                }
                else
                {
                    var color = GUI.color;
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                    EditorGUILayout.LabelField($"✗ {entry.AsmdefName}", "未使用");
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

            // Phase 2: Assets/ アセンブリから DLL 参照マップ構築
            SetProgress("Assets/ アセンブリを収集中...", 0.05f);
            yield return null;

            var assetsAssemblies = CollectAssetsAssemblies(assetsPath);

            // referenceMap: 参照先 asmdef名 → 参照元 asmdef名のセット
            var referenceMap = new Dictionary<string, HashSet<string>>();
            for (var i = 0; i < assetsAssemblies.Count; i++)
            {
                var asm = assetsAssemblies[i];
                SetProgress($"DLL解析中... ({i + 1}/{assetsAssemblies.Count}) {asm.name}",
                    0.1f + 0.2f * i / assetsAssemblies.Count);

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
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MornOptimizer] {asm.name}.dll の参照解析に失敗: {e.Message}");
                }

                yield return null;
            }

            // Phase 3: ソースコードのトークン化 (型名検索用)
            SetProgress("ソースコードをトークン化中...", 0.35f);
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

            // Phase 6: パッケージ内 asmdef 間の依存を解決
            // 使用中 asmdef が参照している同パッケージの asmdef も使用中とマーク
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
                        if (!entry.IsUsed || entry.IsEditorOnly)
                        {
                            continue;
                        }

                        // この asmdef の DLL が参照している asmdef を調べる
                        var dllPath = Path.Combine(scriptAssembliesDir, entry.AsmdefName + ".dll");
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
                                if (!asmdefNames.Contains(refAsm.Name))
                                {
                                    continue;
                                }

                                var dep = group.Entries.FirstOrDefault(e => e.AsmdefName == refAsm.Name);
                                if (dep != null && !dep.IsUsed)
                                {
                                    dep.IsUsed = true;
                                    dep.UsageReason = $"← {entry.AsmdefName}";
                                    changed = true;
                                }
                            }
                        }
                        catch
                        {
                            // ignore
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

            var totalUnused = groups.Sum(g => g.Entries.Count(e => !e.IsUsed));
            Debug.Log($"[MornOptimizer] Asmdef詳細: {groups.Count} パッケージ, {totalUnused} 未使用 asmdef 検出");

            SetProgress("完了", 1f);
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
        }
    }
}
