using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MornLib
{
    public sealed class MornOptimizerUnreferencedTab : MornOptimizerTabBase
    {
        private List<UnreferencedResult> _results;
        private readonly Dictionary<string, bool> _foldouts = new();

        public MornOptimizerUnreferencedTab(EditorWindow owner) : base(owner)
        {
        }

        public override string TabName => "未参照アセット";

        protected override void DrawContent()
        {
            if (GUILayout.Button("スキャン開始", GUILayout.Height(30)))
            {
                _results = null;
                _foldouts.Clear();
                StartAnalysis(AnalyzeCoroutine());
            }

            if (_results == null)
            {
                EditorGUILayout.HelpBox(
                    "「スキャン開始」でAssets/_Develop/内の未参照アセットを検出します。\n" +
                    "※ Resources.Load や Addressables 経由の参照は検出できません。削除前に確認してください。",
                    MessageType.Info);
                return;
            }

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox("未参照アセットはありませんでした。", MessageType.Info);
                return;
            }

            var totalSize = _results.Sum(r => r.FileSize);
            EditorGUILayout.LabelField($"未参照アセット ({_results.Count}件, 合計 {FormatSize(totalSize)})", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Resources.Load や Addressables 経由の参照は検出できません。削除前に確認してください。", MessageType.Warning);
            EditorGUILayout.Space();

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            // 型ごとにグループ化
            var groups = _results.GroupBy(r => r.AssetType).OrderByDescending(g => g.Sum(r => r.FileSize));

            foreach (var group in groups)
            {
                var groupSize = group.Sum(r => r.FileSize);
                var key = group.Key;
                if (!_foldouts.ContainsKey(key))
                {
                    _foldouts[key] = false;
                }

                _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], $"{key} ({group.Count()}件, {FormatSize(groupSize)})", true);

                if (!_foldouts[key])
                {
                    continue;
                }

                EditorGUI.indentLevel++;
                foreach (var result in group.OrderByDescending(r => r.FileSize))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"{result.AssetPath}  ({FormatSize(result.FileSize)})", EditorStyles.miniLabel);
                    if (GUILayout.Button("選択", GUILayout.Width(40)))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<Object>(result.AssetPath);
                        if (obj != null)
                        {
                            EditorGUIUtility.PingObject(obj);
                            Selection.activeObject = obj;
                        }
                    }

                    if (GUILayout.Button("削除", GUILayout.Width(40)))
                    {
                        DeleteAsset(result);
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DeleteAsset(UnreferencedResult result)
        {
            if (!EditorUtility.DisplayDialog(
                    "⚠ アセット削除",
                    $"{result.AssetPath} を削除します ({FormatSize(result.FileSize)})\n\n" +
                    "【警告】この操作は元に戻せません。\n" +
                    "実行前に必ず git の差分を確認し、コミットまたはローカルバックアップを作成してください。\n" +
                    "この操作によって発生した問題について、ツール側では一切の責任を負いません。",
                    "理解した上で削除する",
                    "キャンセル"))
            {
                return;
            }

            if (AssetDatabase.DeleteAsset(result.AssetPath))
            {
                Debug.Log($"[MornOptimizer] 削除しました: {result.AssetPath}");
                _results.Remove(result);
            }
            else
            {
                Debug.LogWarning($"[MornOptimizer] 削除に失敗: {result.AssetPath}");
            }
        }

        private IEnumerator AnalyzeCoroutine()
        {
            SetProgress("アセットを収集中...", 0f);
            yield return null;

            // Assets/_Develop/ 以下の全アセットを収集 (スクリプト除外)
            var skipExtensions = new HashSet<string> { ".cs", ".meta", ".asmdef", ".asmref" };
            var targetPaths = new List<string>();
            var developPath = Path.Combine(Application.dataPath, "_Develop");

            if (!Directory.Exists(developPath))
            {
                _results = new List<UnreferencedResult>();
                yield break;
            }

            var allFiles = Directory.GetFiles(developPath, "*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (skipExtensions.Contains(ext))
                {
                    continue;
                }

                var assetPath = "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/');
                targetPaths.Add(assetPath);
            }

            SetProgress($"シーン・Prefabの依存関係を解析中... ({targetPaths.Count} アセット対象)", 0.1f);
            yield return null;

            // 全シーン + 全Prefab + 全ScriptableObject を起点に依存アセットを収集
            var roots = new List<string>();
            var sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            roots.AddRange(sceneGuids.Select(AssetDatabase.GUIDToAssetPath));

            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            roots.AddRange(prefabGuids.Select(AssetDatabase.GUIDToAssetPath));

            var soGuids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" });
            roots.AddRange(soGuids.Select(AssetDatabase.GUIDToAssetPath));

            // バッチで依存解析
            var referencedAssets = new HashSet<string>();
            var batchSize = 50;
            for (var i = 0; i < roots.Count; i += batchSize)
            {
                SetProgress($"依存関係を解析中... ({i}/{roots.Count})", 0.1f + 0.7f * i / roots.Count);

                var batch = roots.Skip(i).Take(batchSize).ToArray();
                var deps = AssetDatabase.GetDependencies(batch, true);
                foreach (var dep in deps)
                {
                    referencedAssets.Add(dep);
                }

                yield return null;
            }

            SetProgress("未参照アセットを特定中...", 0.85f);
            yield return null;

            // 参照されていないアセットを特定
            var results = new List<UnreferencedResult>();
            foreach (var path in targetPaths)
            {
                if (referencedAssets.Contains(path))
                {
                    continue;
                }

                var fullPath = Path.Combine(Application.dataPath, "..", path);
                long fileSize = 0;
                if (File.Exists(fullPath))
                {
                    fileSize = new FileInfo(fullPath).Length;
                }

                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                var typeName = type?.Name ?? Path.GetExtension(path);

                results.Add(new UnreferencedResult
                {
                    AssetPath = path,
                    AssetType = typeName,
                    FileSize = fileSize,
                });
            }

            _results = results;
            SetProgress("完了", 1f);
        }

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return $"{bytes / (1024f * 1024f):F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024f:F1} KB";
            }

            return $"{bytes} B";
        }

        private class UnreferencedResult
        {
            public string AssetPath;
            public string AssetType;
            public long FileSize;
        }
    }
}
