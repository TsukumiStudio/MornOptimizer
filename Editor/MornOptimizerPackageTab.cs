using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using Assembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MornLib
{
    public sealed class MornOptimizerPackageTab : MornOptimizerTabBase
    {
        public MornOptimizerPackageTab(EditorWindow owner) : base(owner)
        {
        }

        public override string TabName => "パッケージ";
        // packages-lock.json に現れない暗黙的な依存関係
        private static readonly Dictionary<string, string> ImplicitDependencyParent = new()
        {
            { "com.unity.toolchain.macos-arm64-linux-x86_64", "com.unity.burst" },
            { "com.unity.toolchain.win-x86_64-linux-x86_64", "com.unity.burst" },
            { "com.unity.toolchain.linux-x86_64", "com.unity.burst" },
            { "com.unity.sysroot", "com.unity.burst" },
            { "com.unity.sysroot.linux-x86_64", "com.unity.burst" },
        };

        private static readonly Dictionary<string, string[]> ModuleToAssemblyNames = new()
        {
            { "com.unity.modules.accessibility", new[] { "UnityEngine.AccessibilityModule" } },
            { "com.unity.modules.ai", new[] { "UnityEngine.AIModule" } },
            { "com.unity.modules.androidjni", new[] { "UnityEngine.AndroidJNIModule" } },
            { "com.unity.modules.animation", new[] { "UnityEngine.AnimationModule" } },
            { "com.unity.modules.assetbundle", new[] { "UnityEngine.AssetBundleModule" } },
            { "com.unity.modules.audio", new[] { "UnityEngine.AudioModule" } },
            { "com.unity.modules.cloth", new[] { "UnityEngine.ClothModule" } },
            { "com.unity.modules.director", new[] { "UnityEngine.DirectorModule" } },
            { "com.unity.modules.hierarchycore", new[] { "UnityEngine.HierarchyCoreModule" } },
            { "com.unity.modules.imageconversion", new[] { "UnityEngine.ImageConversionModule" } },
            { "com.unity.modules.imgui", new[] { "UnityEngine.IMGUIModule" } },
            { "com.unity.modules.jsonserialize", new[] { "UnityEngine.JSONSerializeModule" } },
            { "com.unity.modules.particlesystem", new[] { "UnityEngine.ParticleSystemModule" } },
            { "com.unity.modules.physics", new[] { "UnityEngine.PhysicsModule" } },
            { "com.unity.modules.physics2d", new[] { "UnityEngine.Physics2DModule" } },
            { "com.unity.modules.screencapture", new[] { "UnityEngine.ScreenCaptureModule" } },
            { "com.unity.modules.subsystems", new[] { "UnityEngine.SubsystemsModule" } },
            { "com.unity.modules.terrain", new[] { "UnityEngine.TerrainModule" } },
            { "com.unity.modules.terrainphysics", new[] { "UnityEngine.TerrainPhysicsModule" } },
            { "com.unity.modules.tilemap", new[] { "UnityEngine.TilemapModule" } },
            { "com.unity.modules.ui", new[] { "UnityEngine.UIModule" } },
            { "com.unity.modules.uielements", new[] { "UnityEngine.UIElementsModule" } },
            { "com.unity.modules.umbra", new[] { "UnityEngine.UmbraModule" } },
            { "com.unity.modules.unityanalytics", new[] { "UnityEngine.UnityAnalyticsModule" } },
            { "com.unity.modules.unitywebrequest", new[] { "UnityEngine.UnityWebRequestModule" } },
            { "com.unity.modules.unitywebrequestassetbundle", new[] { "UnityEngine.UnityWebRequestAssetBundleModule" } },
            { "com.unity.modules.unitywebrequestaudio", new[] { "UnityEngine.UnityWebRequestAudioModule" } },
            { "com.unity.modules.unitywebrequesttexture", new[] { "UnityEngine.UnityWebRequestTextureModule" } },
            { "com.unity.modules.unitywebrequestwww", new[] { "UnityEngine.UnityWebRequestWWWModule" } },
            { "com.unity.modules.vehicles", new[] { "UnityEngine.VehiclesModule" } },
            { "com.unity.modules.video", new[] { "UnityEngine.VideoModule" } },
            { "com.unity.modules.vr", new[] { "UnityEngine.VRModule" } },
            { "com.unity.modules.wind", new[] { "UnityEngine.WindModule" } },
            { "com.unity.modules.xr", new[] { "UnityEngine.XRModule" } },
        };

        // ファイル拡張子の存在で使用判定するパッケージ
        private static readonly Dictionary<string, string[]> PackageToFileExtensions = new()
        {
            { "com.unity.2d.psdimporter", new[] { ".psd" } },
            { "com.unity.2d.aseprite", new[] { ".ase", ".aseprite" } },
        };

        private List<PackageAnalysisResult> _results;
        private Dictionary<string, PackageAnalysisResult> _resultMap;
        private Dictionary<string, LockPackageData> _lockPackages;
        private Dictionary<string, HashSet<string>> _dependedBy;
        private HashSet<string> _cascadeOrphans;
        private bool _selectAllUnused;

        protected override void DrawContent()
        {
            if (GUILayout.Button("解析開始", GUILayout.Height(30)))
            {
                _results = null;
                _selectAllUnused = false;
                StartAnalysis(AnalysisCoroutine());
            }

            if (_results == null)
            {
                EditorGUILayout.HelpBox("「解析開始」を押してパッケージの使用状況を解析してください。", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();

            // 連鎖不要パッケージを計算
            ComputeCascadeOrphans();

            var unusedResults = _results.Where(r => !r.IsUsed).ToList();
            var usedResults = _results.Where(r => r.IsUsed).ToList();

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            EditorGUILayout.LabelField($"未使用パッケージ ({unusedResults.Count}件)", EditorStyles.boldLabel);
            if (unusedResults.Count > 0)
            {
                EditorGUI.BeginChangeCheck();
                _selectAllUnused = EditorGUILayout.ToggleLeft("全て選択", _selectAllUnused);
                if (EditorGUI.EndChangeCheck())
                {
                    foreach (var r in unusedResults)
                    {
                        if (r.CanRemove)
                        {
                            r.Selected = _selectAllUnused;
                        }
                    }
                }

                DrawResultList(unusedResults, DrawUnusedResult);
            }
            else
            {
                EditorGUILayout.HelpBox("未使用パッケージはありません。", MessageType.Info);
            }

            EditorGUILayout.Space();

            EditorGUILayout.LabelField($"使用中パッケージ ({usedResults.Count}件)", EditorStyles.boldLabel);
            DrawResultList(usedResults, DrawUsedResultOrCascade);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            var selectedCount = _results.Count(r => r.Selected);
            if (selectedCount > 0)
            {
                var buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button($"選択した {selectedCount} 件を削除", buttonStyle, GUILayout.Height(30)))
                {
                    RemoveSelectedPackages(_results.Where(r => r.Selected).ToList());
                }

                GUI.backgroundColor = Color.white;
            }
        }

        private void ComputeCascadeOrphans()
        {
            _cascadeOrphans ??= new HashSet<string>();
            _cascadeOrphans.Clear();

            if (_results == null || _lockPackages == null || _dependedBy == null)
            {
                return;
            }

            // ユーザーが選択したパッケージ (未使用 + カスケードで選んだもの全て)
            var removing = new HashSet<string>();
            foreach (var r in _results)
            {
                if (r.Selected)
                {
                    removing.Add(r.PackageName);
                }
            }

            if (removing.Count == 0)
            {
                foreach (var r in _results)
                {
                    if (r.IsUsed && r.Selected)
                    {
                        r.Selected = false;
                    }
                }

                return;
            }

            // removing + cascadeOrphans の依存先をたどり、孤立するものを検出
            var changed = true;
            while (changed)
            {
                changed = false;
                var toCheck = new HashSet<string>(removing);
                toCheck.UnionWith(_cascadeOrphans);

                foreach (var pkgName in toCheck)
                {
                    if (!_lockPackages.TryGetValue(pkgName, out var pkgData))
                    {
                        continue;
                    }

                    foreach (var depName in pkgData.DependencyNames)
                    {
                        if (_cascadeOrphans.Contains(depName))
                        {
                            continue;
                        }

                        // コード/アセット/型で直接使用されているパッケージはカスケード対象外
                        if (_resultMap.TryGetValue(depName, out var depResult) && depResult.DirectlyUsed)
                        {
                            continue;
                        }

                        if (!_dependedBy.TryGetValue(depName, out var users))
                        {
                            continue;
                        }

                        if (users.All(u => removing.Contains(u) || _cascadeOrphans.Contains(u)))
                        {
                            _cascadeOrphans.Add(depName);
                            changed = true;
                        }
                    }
                }
            }

            // カスケード対象から外れた使用中パッケージの選択を解除
            foreach (var r in _results)
            {
                if (r.IsUsed && r.Selected && !_cascadeOrphans.Contains(r.PackageName))
                {
                    r.Selected = false;
                }
            }
        }

        // ── 解析コルーチン ──

        // ── 解析コルーチン ──

        private IEnumerator AnalysisCoroutine()
        {
            // Phase 1: パッケージ一覧取得
            SetProgress("パッケージ一覧を取得中...", 0f);
            yield return null;

            var assetsPath = Application.dataPath;
            var scriptAssembliesDir = Path.Combine(assetsPath, "..", "Library", "ScriptAssemblies");

            // packages-lock.json から全インストール済みパッケージを取得
            var lockPackages = ParsePackagesLock(Path.Combine(assetsPath, "..", "Packages", "packages-lock.json"));
            Debug.Log($"[MornOptimizer] {lockPackages.Count} パッケージを検出");

            // PackageInfo (displayName, resolvedPath) を収集
            // Client.List は一部パッケージを返さないため、FindForPackageName でフォールバック
            SetProgress("パッケージ情報を収集中...", 0.03f);
            yield return null;

            var packageInfoMap = new Dictionary<string, PackageInfo>();
            foreach (var pkgName in lockPackages.Keys)
            {
                var pi = PackageInfo.FindForPackageName(pkgName);
                if (pi != null)
                {
                    packageInfoMap[pkgName] = pi;
                }
            }

            // Phase 2: Assets/ アセンブリ収集
            SetProgress("Assets/ アセンブリを収集中...", 0.05f);
            yield return null;

            var assetsAssemblies = CollectAssetsAssemblies(assetsPath);

            // Phase 3: DLL参照マップ構築 (1DLLずつ)
            var referenceMap = new Dictionary<string, HashSet<string>>();
            for (var i = 0; i < assetsAssemblies.Count; i++)
            {
                var asm = assetsAssemblies[i];
                SetProgress($"DLL解析中... ({i + 1}/{assetsAssemblies.Count}) {asm.name}", 0.1f + 0.2f * i / assetsAssemblies.Count);

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

            // Phase 4: パッケージ使用判定
            SetProgress("パッケージ使用状況を判定中...", 0.3f);
            yield return null;

            var manifestPackageIds = GetManifestPackageIds();

            // Feature パッケージを展開: サブパッケージを個別解析対象に追加
            var featureSubPackages = new Dictionary<string, List<string>>();
            var subPackageToFeature = new Dictionary<string, string>();

            foreach (var kvp in lockPackages)
            {
                if (!kvp.Key.StartsWith("com.unity.feature."))
                {
                    continue;
                }

                var subNames = new List<string>();
                foreach (var depName in kvp.Value.DependencyNames)
                {
                    subNames.Add(depName);
                    subPackageToFeature[depName] = kvp.Key;
                }

                featureSubPackages[kvp.Key] = subNames;
            }

            // 使用判定: Assets/ アセンブリから参照されているか
            var usedPackageNames = new HashSet<string>();
            var packageToReferencingAssemblies = new Dictionary<string, HashSet<string>>();
            foreach (var kvp in lockPackages)
            {
                var pkgName = kvp.Key;
                var pkgData = kvp.Value;
                var pi = packageInfoMap.GetValueOrDefault(pkgName);
                var referencingAssemblies = GetReferencingAssembliesFromLock(pkgName, pkgData, pi, referenceMap, scriptAssembliesDir);
                if (referencingAssemblies.Count > 0)
                {
                    usedPackageNames.Add(pkgName);
                    packageToReferencingAssemblies[pkgName] = referencingAssemblies;
                }
            }

            // 依存チェーン (packages-lock.json レベル)
            var allNeededPackages = new HashSet<string>(usedPackageNames);
            var dependencyParents = new Dictionary<string, string>();
            var queue = new Queue<string>(usedPackageNames);
            while (queue.Count > 0)
            {
                var pkgName = queue.Dequeue();
                if (!lockPackages.TryGetValue(pkgName, out var pkgData))
                {
                    continue;
                }

                foreach (var depName in pkgData.DependencyNames)
                {
                    if (allNeededPackages.Add(depName))
                    {
                        dependencyParents[depName] = pkgName;
                        queue.Enqueue(depName);
                    }
                }
            }

            // Phase 4.5: 非モジュールパッケージの DLL からモジュール依存を検出
            var moduleAssemblyToPackage = new Dictionary<string, string>();
            foreach (var kvp in ModuleToAssemblyNames)
            {
                foreach (var asmName in kvp.Value)
                {
                    moduleAssemblyToPackage[asmName] = kvp.Key;
                }
            }

            var dllScanPackages = lockPackages
                .Where(kvp => !kvp.Key.StartsWith("com.unity.modules."))
                .ToArray();

            for (var i = 0; i < dllScanPackages.Length; i++)
            {
                var pkgName = dllScanPackages[i].Key;
                var pi = packageInfoMap.GetValueOrDefault(pkgName);
                SetProgress($"パッケージDLL依存を解析中... ({i + 1}/{dllScanPackages.Length}) {pkgName}",
                    0.3f + 0.05f * i / dllScanPackages.Length);

                var resolvedPath = pi?.resolvedPath;
                if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
                {
                    continue;
                }

                var asmdefFiles = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories);
                foreach (var asmdefFile in asmdefFiles)
                {
                    var asmdefJson = File.ReadAllText(asmdefFile);
                    var asmName = ExtractAsmdefName(asmdefJson);
                    if (string.IsNullOrEmpty(asmName))
                    {
                        continue;
                    }

                    // Editor専用アセンブリはスキップ (ランタイム依存ではない)
                    if (IsEditorOnlyAsmdef(asmdefJson))
                    {
                        continue;
                    }

                    var dllPath = Path.Combine(scriptAssembliesDir, asmName + ".dll");
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
                            if (!moduleAssemblyToPackage.TryGetValue(refAsm.Name, out var modulePkgName))
                            {
                                continue;
                            }

                            if (allNeededPackages.Add(modulePkgName))
                            {
                                dependencyParents[modulePkgName] = pkgName;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                yield return null;
            }

            // Phase 5: ソースファイル読み込み (1アセンブリずつ)
            var sourceTexts = new Dictionary<string, string>();
            for (var i = 0; i < assetsAssemblies.Count; i++)
            {
                var asm = assetsAssemblies[i];
                SetProgress($"ソースファイル読み込み中... ({i + 1}/{assetsAssemblies.Count}) {asm.name}", 0.35f + 0.15f * i / assetsAssemblies.Count);

                var texts = new List<string>();
                foreach (var sourceFile in asm.sourceFiles)
                {
                    var fullPath = Path.GetFullPath(sourceFile);
                    if (!fullPath.StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(sourceFile))
                    {
                        texts.Add(File.ReadAllText(sourceFile));
                    }
                }

                if (texts.Count > 0)
                {
                    sourceTexts[asm.name] = string.Join("\n", texts);
                }

                yield return null;
            }

            // Phase 6: エクスポート型名の収集 (1パッケージずつ)
            var packageTypeNamesCache = new Dictionary<string, HashSet<string>>();
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var targetPackageNames = lockPackages.Keys.ToArray();
            for (var i = 0; i < targetPackageNames.Length; i++)
            {
                var pkgName = targetPackageNames[i];
                var pi = packageInfoMap.GetValueOrDefault(pkgName);
                SetProgress($"型情報を収集中... ({i + 1}/{targetPackageNames.Length}) {pkgName}", 0.5f + 0.1f * i / targetPackageNames.Length);

                var typeNames = GetPackageExportedTypeNamesFromLock(pkgName, pi, scriptAssembliesDir, loadedAssemblies);
                if (typeNames.Count > 0)
                {
                    packageTypeNamesCache[pkgName] = typeNames;
                }

                yield return null;
            }

            // Phase 7: ソースコードのトークン化 (高速な型名検索用)
            SetProgress("ソースコードをトークン化中...", 0.6f);
            yield return null;

            var sourceWordSet = new HashSet<string>();
            foreach (var text in sourceTexts.Values)
            {
                foreach (Match m in Regex.Matches(text, @"\b[A-Z]\w+\b"))
                {
                    sourceWordSet.Add(m.Value);
                }
            }

            // Phase 8: Scene/Prefab スキャン (1ファイルずつ、トークン比較)
            var assetFiles = Directory.GetFiles(assetsPath, "*.unity", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(assetsPath, "*.prefab", SearchOption.AllDirectories))
                .ToArray();

            // 全パッケージの型名 → パッケージ名の逆引きマップ (asmdef由来 + builtin手動マッピング)
            var typeToPackage = new Dictionary<string, string>();
            foreach (var kvp in packageTypeNamesCache)
            {
                foreach (var typeName in kvp.Value)
                {
                    typeToPackage.TryAdd(typeName, kvp.Key);
                }
            }


            var assetFileUsage = new Dictionary<string, AssetFileUsageInfo>();
            for (var i = 0; i < assetFiles.Length; i++)
            {
                var filePath = assetFiles[i];
                var fileName = Path.GetFileName(filePath);
                SetProgress($"アセットスキャン中... ({i + 1}/{assetFiles.Length}) {fileName}", 0.65f + 0.2f * i / assetFiles.Length);

                var text = File.ReadAllText(filePath);

                // Unity YAMLでコンポーネント宣言はインデントなし (プロパティはインデントあり)
                foreach (Match m in Regex.Matches(text, @"^(\w+):", RegexOptions.Multiline))
                {
                    var componentName = m.Groups[1].Value;
                    if (!typeToPackage.TryGetValue(componentName, out var pkgName))
                    {
                        continue;
                    }

                    if (assetFileUsage.ContainsKey(pkgName))
                    {
                        continue;
                    }

                    assetFileUsage[pkgName] = new AssetFileUsageInfo { FileName = fileName, TypeName = componentName };
                }

                yield return null;
            }

            // Phase 8.5: ファイル拡張子スキャン (.psd → psdimporter, .ase → aseprite 等)
            SetProgress("ファイル拡張子をスキャン中...", 0.86f);
            yield return null;

            foreach (var kvp in PackageToFileExtensions)
            {
                if (assetFileUsage.ContainsKey(kvp.Key))
                {
                    continue;
                }

                foreach (var ext in kvp.Value)
                {
                    var files = Directory.GetFiles(assetsPath, $"*{ext}", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        assetFileUsage[kvp.Key] = new AssetFileUsageInfo
                        {
                            FileName = Path.GetFileName(files[0]),
                            TypeName = $"{ext} ファイル ({files.Length}件)",
                        };
                        break;
                    }
                }
            }

            // ソースコードで Sprite 型を使っていれば com.unity.2d.sprite も必要
            if (!assetFileUsage.ContainsKey("com.unity.2d.sprite") && sourceWordSet.Contains("Sprite"))
            {
                assetFileUsage["com.unity.2d.sprite"] = new AssetFileUsageInfo
                {
                    FileName = "(ソースコード)",
                    TypeName = "Sprite",
                };
            }

            foreach (var kvp in assetFileUsage)
            {
                usedPackageNames.Add(kvp.Key);
            }

            // Phase 8.7: コンパイル済みDLLの型使用によるフォールバック検出
            // GetReferencedAssemblies() で検出できないモジュール依存を、
            // DLL内の型が参照するアセンブリ名から検出する (型フォワーディング対策)
            SetProgress("DLL型参照を解析中...", 0.88f);
            yield return null;

            var moduleAssemblyNameSet = new HashSet<string>();
            foreach (var kvp in ModuleToAssemblyNames)
            {
                foreach (var asmName in kvp.Value)
                {
                    moduleAssemblyNameSet.Add(asmName);
                }
            }

            // Assets/ アセンブリのDLLをスキャン
            foreach (var asm in assetsAssemblies)
            {
                var dllPath = Path.Combine(scriptAssembliesDir, asm.name + ".dll");
                if (!File.Exists(dllPath))
                {
                    continue;
                }

                try
                {
                    var bytes = File.ReadAllBytes(dllPath);
                    var loadedAsm = System.Reflection.Assembly.Load(bytes);
                    foreach (var type in loadedAsm.GetTypes())
                    {
                        CheckTypeModuleUsage(type, moduleAssemblyToPackage, usedPackageNames);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // パッケージDLLもスキャン (UniRx等がモジュール型を使うケース)
            var scannedDlls = new HashSet<string>(assetsAssemblies.Select(a => a.name));
            foreach (var kvp in lockPackages)
            {
                if (kvp.Key.StartsWith("com.unity.modules."))
                {
                    continue;
                }

                var pi = packageInfoMap.GetValueOrDefault(kvp.Key);
                var resolvedPath = pi?.resolvedPath;
                if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
                {
                    continue;
                }

                var asmdefFiles = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories);
                foreach (var asmdefFile in asmdefFiles)
                {
                    var asmdefJson = File.ReadAllText(asmdefFile);
                    var asmName = ExtractAsmdefName(asmdefJson);
                    if (string.IsNullOrEmpty(asmName) || scannedDlls.Contains(asmName))
                    {
                        continue;
                    }

                    if (IsEditorOnlyAsmdef(asmdefJson))
                    {
                        continue;
                    }

                    var dllPath = Path.Combine(scriptAssembliesDir, asmName + ".dll");
                    if (!File.Exists(dllPath))
                    {
                        continue;
                    }

                    scannedDlls.Add(asmName);

                    try
                    {
                        var bytes = File.ReadAllBytes(dllPath);
                        var loadedAsm = System.Reflection.Assembly.Load(bytes);
                        var foundModules = new HashSet<string>();
                        foreach (var type in loadedAsm.GetTypes())
                        {
                            CheckTypeModuleUsage(type, moduleAssemblyToPackage, foundModules);
                        }

                        // 見つかったモジュールを依存チェーンに追加
                        foreach (var modulePkg in foundModules)
                        {
                            usedPackageNames.Add(modulePkg);
                            allNeededPackages.Add(modulePkg);
                            dependencyParents.TryAdd(modulePkg, kvp.Key);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            // 逆引きマップ構築 (結果組み立てで使用)
            var localDependedBy = new Dictionary<string, HashSet<string>>();
            foreach (var kvp in lockPackages)
            {
                foreach (var depName in kvp.Value.DependencyNames)
                {
                    if (!localDependedBy.TryGetValue(depName, out var set))
                    {
                        set = new HashSet<string>();
                        localDependedBy[depName] = set;
                    }

                    set.Add(kvp.Key);
                }
            }

            // 暗黙的依存を注入 (packages-lock.json に現れないが実際に必要な依存)
            foreach (var kvp in ImplicitDependencyParent)
            {
                if (!lockPackages.ContainsKey(kvp.Key) || !lockPackages.ContainsKey(kvp.Value))
                {
                    continue;
                }

                if (!localDependedBy.TryGetValue(kvp.Key, out var set))
                {
                    set = new HashSet<string>();
                    localDependedBy[kvp.Key] = set;
                }

                set.Add(kvp.Value);

                // 依存チェーンにも追加
                if (allNeededPackages.Contains(kvp.Value))
                {
                    allNeededPackages.Add(kvp.Key);
                    dependencyParents.TryAdd(kvp.Key, kvp.Value);
                }
            }

            // Phase 9: 結果組み立て
            SetProgress("結果を生成中...", 0.9f);
            yield return null;

            var results = new List<PackageAnalysisResult>();

            foreach (var kvp in lockPackages)
            {
                var pkgName = kvp.Key;
                var pkgData = kvp.Value;

                // Feature パッケージ自体はスキップ (サブパッケージを個別表示)
                if (featureSubPackages.ContainsKey(pkgName))
                {
                    continue;
                }


                var directlyUsed = usedPackageNames.Contains(pkgName);
                var neededAsDependency = !directlyUsed && allNeededPackages.Contains(pkgName);
                var isUsed = directlyUsed || neededAsDependency;

                var isInManifest = manifestPackageIds.Contains(pkgName);
                var isFeatureSub = subPackageToFeature.ContainsKey(pkgName);
                var canRemove = isInManifest || isFeatureSub;

                // 理由構築: パッケージ依存元を最優先、Assets/参照は補足
                var reasonParts = new List<string>();

                // パッケージ依存元
                if (localDependedBy.TryGetValue(pkgName, out var depUsers) && depUsers.Count > 0)
                {
                    var userNames = depUsers
                        .Where(u => lockPackages.ContainsKey(u))
                        .Select(u => packageInfoMap.TryGetValue(u, out var ui) ? ui.displayName : u)
                        .OrderBy(x => x)
                        .ToList();
                    if (userNames.Count > 0)
                    {
                        reasonParts.Add($"← {string.Join(", ", userNames)}");
                    }
                }

                // Assets/ コード参照
                if (directlyUsed)
                {
                    var refAssemblies = packageToReferencingAssemblies.GetValueOrDefault(pkgName);
                    var codeTypeNames = FindUsedTypeNamesFromSet(pkgName, packageTypeNamesCache, sourceWordSet);
                    var assetInfo = assetFileUsage.GetValueOrDefault(pkgName);
                    var directReason = BuildDirectUsageReason(refAssemblies, codeTypeNames, assetInfo);
                    if (!string.IsNullOrEmpty(directReason))
                    {
                        reasonParts.Add(directReason);
                    }
                }

                // dependencyParents からのフォールバック (Phase 4.5 で検出された場合)
                if (reasonParts.Count == 0 && neededAsDependency &&
                    dependencyParents.TryGetValue(pkgName, out var parentPkg))
                {
                    var parentDisplay = packageInfoMap.TryGetValue(parentPkg, out var ppi) ? ppi.displayName : parentPkg;
                    reasonParts.Add($"← {parentDisplay} (DLL参照)");
                }

                var reason = reasonParts.Count > 0 ? string.Join(" | ", reasonParts) : "";

                if (!directlyUsed && !neededAsDependency && !canRemove)
                {
                    isUsed = true;
                }

                string featureGroup = null;
                string featurePackageName = null;
                if (subPackageToFeature.TryGetValue(pkgName, out var featureName))
                {
                    featurePackageName = featureName;
                    var featureDisplay = packageInfoMap.TryGetValue(featureName, out var fi) ? fi.displayName : featureName;
                    featureGroup = featureDisplay;
                }

                var pi = packageInfoMap.GetValueOrDefault(pkgName);
                results.Add(new PackageAnalysisResult
                {
                    PackageName = pkgName,
                    DisplayName = pi?.displayName ?? pkgName,
                    Version = pkgData.Version,
                    IsUsed = isUsed,
                    DirectlyUsed = directlyUsed,
                    CanRemove = canRemove,
                    UsageReason = reason,
                    Selected = false,
                    FeatureGroup = featureGroup,
                    FeaturePackageName = featurePackageName,
                });
            }

            results.Sort((a, b) =>
            {
                if (a.IsUsed != b.IsUsed)
                {
                    return a.IsUsed ? 1 : -1;
                }

                // Feature グループ内でまとめる
                var ga = a.FeatureGroup ?? "";
                var gb = b.FeatureGroup ?? "";
                var gc = string.Compare(ga, gb, StringComparison.Ordinal);
                if (gc != 0)
                {
                    return gc;
                }

                return string.Compare(a.PackageName, b.PackageName, StringComparison.Ordinal);
            });

            _results = results;
            _lockPackages = lockPackages;

            _resultMap = new Dictionary<string, PackageAnalysisResult>();
            foreach (var r in results)
            {
                _resultMap[r.PackageName] = r;
            }

            _dependedBy = new Dictionary<string, HashSet<string>>();
            foreach (var kvp in lockPackages)
            {
                foreach (var depName in kvp.Value.DependencyNames)
                {
                    if (!_dependedBy.TryGetValue(depName, out var set))
                    {
                        set = new HashSet<string>();
                        _dependedBy[depName] = set;
                    }

                    set.Add(kvp.Key);
                }
            }

            _cascadeOrphans = new HashSet<string>();
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

        private HashSet<string> GetReferencingAssembliesFromLock(
            string pkgName, LockPackageData pkgData, PackageInfo pi,
            Dictionary<string, HashSet<string>> referenceMap, string scriptAssembliesDir)
        {
            var result = new HashSet<string>();

            // モジュールパッケージ
            if (ModuleToAssemblyNames.TryGetValue(pkgName, out var moduleAssemblies))
            {
                foreach (var modAsm in moduleAssemblies)
                {
                    if (referenceMap.TryGetValue(modAsm, out var refs))
                    {
                        result.UnionWith(refs);
                    }
                }

                return result;
            }

            // 非モジュール: resolvedPath から asmdef を探す
            var resolvedPath = pi?.resolvedPath;
            if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
            {
                return result;
            }

            var asmdefFiles = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories);
            foreach (var asmdefFile in asmdefFiles)
            {
                var asmdefName = ExtractAsmdefName(File.ReadAllText(asmdefFile));
                if (!string.IsNullOrEmpty(asmdefName) && referenceMap.TryGetValue(asmdefName, out var refs))
                {
                    result.UnionWith(refs);
                }
            }

            return result;
        }

        private void CheckTypeModuleUsage(
            Type type,
            Dictionary<string, string> moduleAssemblyToPackage,
            HashSet<string> usedPackageNames)
        {
            try
            {
                // ベースクラスのアセンブリ
                var baseType = type.BaseType;
                if (baseType != null)
                {
                    var asmName = baseType.Assembly.GetName().Name;
                    if (moduleAssemblyToPackage.TryGetValue(asmName, out var pkg))
                    {
                        usedPackageNames.Add(pkg);
                    }
                }

                // フィールドの型のアセンブリ
                foreach (var field in type.GetFields(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.DeclaredOnly))
                {
                    var asmName = field.FieldType.Assembly.GetName().Name;
                    if (moduleAssemblyToPackage.TryGetValue(asmName, out var pkg))
                    {
                        usedPackageNames.Add(pkg);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private HashSet<string> GetPackageExportedTypeNamesFromLock(
            string pkgName, PackageInfo pi,
            string scriptAssembliesDir, System.Reflection.Assembly[] loadedAssemblies)
        {
            var assemblyNames = new List<string>();

            if (ModuleToAssemblyNames.TryGetValue(pkgName, out var moduleAssemblies))
            {
                assemblyNames.AddRange(moduleAssemblies);
            }
            else
            {
                var resolvedPath = pi?.resolvedPath;
                if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
                {
                    return new HashSet<string>();
                }

                var asmdefFiles = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories);
                foreach (var asmdefFile in asmdefFiles)
                {
                    var name = ExtractAsmdefName(File.ReadAllText(asmdefFile));
                    if (!string.IsNullOrEmpty(name))
                    {
                        assemblyNames.Add(name);
                    }
                }
            }

            var typeNames = new HashSet<string>();

            foreach (var asmName in assemblyNames)
            {
                var loaded = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == asmName);
                if (loaded == null)
                {
                    var dllPath = Path.Combine(scriptAssembliesDir, asmName + ".dll");
                    if (File.Exists(dllPath))
                    {
                        try
                        {
                            loaded = System.Reflection.Assembly.Load(File.ReadAllBytes(dllPath));
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                if (loaded == null)
                {
                    continue;
                }

                try
                {
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
            }

            return typeNames;
        }

        private List<string> FindUsedTypeNamesFromSet(
            string packageName,
            Dictionary<string, HashSet<string>> packageTypeNamesCache,
            HashSet<string> sourceWordSet)
        {
            if (!packageTypeNamesCache.TryGetValue(packageName, out var typeNames))
            {
                return new List<string>();
            }

            var found = new List<string>();
            foreach (var typeName in typeNames)
            {
                if (sourceWordSet.Contains(typeName))
                {
                    found.Add(typeName);
                    if (found.Count >= 2)
                    {
                        break;
                    }
                }
            }

            return found;
        }

        private string BuildDirectUsageReason(
            HashSet<string> referencingAssemblies,
            List<string> codeTypeNames,
            AssetFileUsageInfo assetInfo)
        {
            var parts = new List<string>();

            if (referencingAssemblies != null && referencingAssemblies.Count > 0)
            {
                parts.Add(string.Join(", ", referencingAssemblies.OrderBy(x => x)));
                if (codeTypeNames.Count > 0)
                {
                    parts.Add("(" + string.Join(", ", codeTypeNames) + ")");
                }
            }

            if (assetInfo != null)
            {
                parts.Add($"[{assetInfo.FileName}: {assetInfo.TypeName}]");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "Assets/ から参照あり";
        }

        private HashSet<string> GetManifestPackageIds()
        {
            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
            var manifestJson = File.ReadAllText(manifestPath);
            var ids = new HashSet<string>();

            var lines = manifestJson.Split('\n');
            var inDependencies = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
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

                if (trimmed.StartsWith("\""))
                {
                    var endQuote = trimmed.IndexOf('"', 1);
                    if (endQuote > 1)
                    {
                        ids.Add(trimmed.Substring(1, endQuote - 1));
                    }
                }
            }

            return ids;
        }

        private bool IsEditorOnlyAsmdef(string json)
        {
            // "includePlatforms": ["Editor"] を検出
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

        // ── UI描画 ──

        private void DrawResultList(List<PackageAnalysisResult> list, Action<PackageAnalysisResult> drawItem)
        {
            string lastGroup = null;
            foreach (var result in list)
            {
                var group = result.FeatureGroup;
                if (group != lastGroup)
                {
                    if (group != null)
                    {
                        EditorGUILayout.LabelField($"  ▼ {group}", EditorStyles.miniLabel);
                    }

                    lastGroup = group;
                }

                drawItem(result);
            }
        }

        private void DrawUnusedResult(PackageAnalysisResult result)
        {
            var indent = result.FeatureGroup != null ? "    " : "";
            if (result.CanRemove)
            {
                result.Selected = EditorGUILayout.ToggleLeft(
                    $"{indent}{result.DisplayName}  ({result.PackageName})",
                    result.Selected);
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField($"  {indent}{result.DisplayName}  ({result.PackageName})  — 依存パッケージ (自動)");
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawUsedResultOrCascade(PackageAnalysisResult result)
        {
            var indent = result.FeatureGroup != null ? "    " : "";
            var isCascade = _cascadeOrphans != null && _cascadeOrphans.Contains(result.PackageName);

            if (isCascade)
            {
                // 連鎖で不要になるパッケージ → チェックボックス表示
                var label = $"{indent}{result.DisplayName}  ({result.PackageName})  — 削除で不要になる";
                var prev = result.Selected;
                result.Selected = EditorGUILayout.ToggleLeft(label, result.Selected);
                if (result.Selected != prev)
                {
                    Repaint();
                }
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField($"  {indent}{result.DisplayName}  ({result.PackageName})  — {result.UsageReason}");
                EditorGUI.EndDisabledGroup();
            }
        }

        // ── パッケージ削除 ──

        private void RemoveSelectedPackages(List<PackageAnalysisResult> toRemove)
        {
            // Feature サブパッケージの削除がある場合、Feature 自体を解体する
            var featuresToDissolve = new HashSet<string>();
            var featureUsedSubs = new Dictionary<string, List<PackageAnalysisResult>>();

            foreach (var r in toRemove)
            {
                if (r.FeaturePackageName == null)
                {
                    continue;
                }

                featuresToDissolve.Add(r.FeaturePackageName);
            }

            // 各 Feature について、使用中のサブパッケージを収集
            if (featuresToDissolve.Count > 0 && _results != null)
            {
                foreach (var r in _results)
                {
                    if (r.FeaturePackageName == null || !featuresToDissolve.Contains(r.FeaturePackageName))
                    {
                        continue;
                    }

                    if (r.IsUsed || !r.Selected)
                    {
                        if (!featureUsedSubs.TryGetValue(r.FeaturePackageName, out var list))
                        {
                            list = new List<PackageAnalysisResult>();
                            featureUsedSubs[r.FeaturePackageName] = list;
                        }

                        list.Add(r);
                    }
                }
            }

            // 確認メッセージ作成
            var msgLines = new List<string>();
            var removeNames = toRemove.Where(r => r.FeaturePackageName == null).Select(r => $"  - {r.DisplayName} ({r.PackageName})");
            msgLines.AddRange(removeNames);

            foreach (var featureName in featuresToDissolve)
            {
                msgLines.Add($"\n  [Feature 解体] {featureName}:");
                var subs = toRemove.Where(r => r.FeaturePackageName == featureName);
                foreach (var s in subs)
                {
                    msgLines.Add($"    削除: {s.DisplayName}");
                }

                if (featureUsedSubs.TryGetValue(featureName, out var kept))
                {
                    foreach (var k in kept)
                    {
                        msgLines.Add($"    残す: {k.DisplayName}");
                    }
                }
            }

            if (!EditorUtility.DisplayDialog(
                    "パッケージ削除確認",
                    $"以下の変更を manifest.json に適用します:\n\n{string.Join("\n", msgLines)}\n\n続行しますか？",
                    "実行する",
                    "キャンセル"))
            {
                return;
            }

            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
            var manifestText = File.ReadAllText(manifestPath);

            // 削除対象: 選択されたパッケージ + 解体する Feature パッケージ
            var removeFromManifest = new HashSet<string>(toRemove.Select(r => r.PackageName));
            foreach (var featureName in featuresToDissolve)
            {
                removeFromManifest.Add(featureName);
            }

            var lines = manifestText.Split('\n').ToList();
            var linesToRemove = new List<int>();
            var inDependencies = false;
            var lastDependencyLine = -1;

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

                if (trimmed.StartsWith("\""))
                {
                    lastDependencyLine = i;
                }

                if (removeFromManifest.Any(pkgName => trimmed.Contains($"\"{pkgName}\"")))
                {
                    linesToRemove.Add(i);
                }
            }

            for (var i = linesToRemove.Count - 1; i >= 0; i--)
            {
                lines.RemoveAt(linesToRemove[i]);
            }

            // Feature の使用中サブパッケージを個別に追加
            var addLines = new List<string>();
            foreach (var kvp in featureUsedSubs)
            {
                foreach (var sub in kvp.Value)
                {
                    addLines.Add($"    \"{sub.PackageName}\": \"{sub.Version}\",");
                }
            }

            if (addLines.Count > 0)
            {
                // dependencies ブロックの閉じ括弧の前に挿入
                var insertIndex = -1;
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
                        insertIndex = i;
                        break;
                    }
                }

                if (insertIndex >= 0)
                {
                    // 直前の行にカンマを追加
                    for (var i = insertIndex - 1; i >= 0; i--)
                    {
                        var trimmed = lines[i].Trim();
                        if (trimmed.StartsWith("\""))
                        {
                            if (!lines[i].TrimEnd().EndsWith(","))
                            {
                                lines[i] = lines[i].TrimEnd() + ",";
                            }

                            break;
                        }
                    }

                    lines.InsertRange(insertIndex, addLines);
                }
            }

            FixTrailingCommas(lines);

            File.WriteAllText(manifestPath, string.Join("\n", lines));

            var removedCount = toRemove.Count;
            var addedCount = addLines.Count;
            var msg = $"[MornOptimizer] {removedCount} 件削除";
            if (featuresToDissolve.Count > 0)
            {
                msg += $", {featuresToDissolve.Count} Feature 解体";
            }

            if (addedCount > 0)
            {
                msg += $", {addedCount} 件を個別追加";
            }

            Debug.Log(msg + "。Unityがパッケージを再解決します。");

            Client.Resolve();
            _results = null;
        }

        private void FixTrailingCommas(List<string> lines)
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

        private Dictionary<string, LockPackageData> ParsePackagesLock(string lockPath)
        {
            var result = new Dictionary<string, LockPackageData>();
            if (!File.Exists(lockPath))
            {
                return result;
            }

            var json = File.ReadAllText(lockPath);

            // "dependencies" ブロックの各パッケージをパース
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

            // トップレベル dependencies の各エントリをパース
            var depth = 0;
            var i = braceStart;
            var entryStart = -1;
            var currentName = "";

            while (i < json.Length)
            {
                var c = json[i];
                if (c == '"' && depth == 1 && entryStart < 0)
                {
                    // パッケージ名を読む
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
                        var data = ParseLockEntry(entryJson);
                        result[currentName] = data;
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

            // version
            var vIdx = entryJson.IndexOf("\"version\"", StringComparison.Ordinal);
            if (vIdx >= 0)
            {
                var q1 = entryJson.IndexOf('"', entryJson.IndexOf(':', vIdx) + 1);
                var q2 = entryJson.IndexOf('"', q1 + 1);
                if (q1 >= 0 && q2 > q1)
                {
                    data.Version = entryJson.Substring(q1 + 1, q2 - q1 - 1);
                }
            }

            // source
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

            // dependencies (名前のみ)
            var dIdx = entryJson.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (dIdx >= 0)
            {
                var dBrace = entryJson.IndexOf('{', dIdx);
                var dEnd = entryJson.IndexOf('}', dBrace);
                if (dBrace >= 0 && dEnd > dBrace)
                {
                    var depsBlock = entryJson.Substring(dBrace + 1, dEnd - dBrace - 1);
                    foreach (var line in depsBlock.Split(','))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("\""))
                        {
                            var endQ = trimmed.IndexOf('"', 1);
                            if (endQ > 1)
                            {
                                data.DependencyNames.Add(trimmed.Substring(1, endQ - 1));
                            }
                        }
                    }
                }
            }

            return data;
        }

        // ── データクラス ──

        private class LockPackageData
        {
            public string Version = "";
            public string Source = "";
            public List<string> DependencyNames = new();
        }

        private class PackageAnalysisResult
        {
            public string PackageName;
            public string DisplayName;
            public string Version;
            public bool IsUsed;
            public bool DirectlyUsed;          // コード/アセット/型リフレクションで直接使用検出
            public bool CanRemove;             // manifest/Feature から削除可能か
            public string UsageReason;
            public bool Selected;
            public string FeatureGroup;        // Feature パッケージの表示名 (null = 非Feature)
            public string FeaturePackageName;  // Feature パッケージ名 (null = 非Feature)
        }

        private class AssetFileUsageInfo
        {
            public string FileName;
            public string TypeName;
        }
    }
}
