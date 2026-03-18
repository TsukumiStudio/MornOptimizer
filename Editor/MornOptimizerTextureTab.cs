using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MornLib
{
    public sealed class MornOptimizerTextureTab : MornOptimizerTabBase
    {
        private List<TextureResult> _results;
        private bool _selectAll;

        public MornOptimizerTextureTab(EditorWindow owner) : base(owner)
        {
        }

        public override string TabName => "テクスチャ";

        protected override void DrawContent()
        {
            if (GUILayout.Button("スキャン開始", GUILayout.Height(30)))
            {
                _results = null;
                _selectAll = false;
                StartAnalysis(AnalyzeCoroutine());
            }

            if (_results == null)
            {
                EditorGUILayout.HelpBox("「スキャン開始」でAssets/内のテクスチャ設定を解析します。", MessageType.Info);
                return;
            }

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox("問題のあるテクスチャはありませんでした。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"最適化可能なテクスチャ ({_results.Count}件)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            _selectAll = EditorGUILayout.ToggleLeft("全て選択", _selectAll);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var r in _results)
                {
                    r.Selected = _selectAll;
                }
            }

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            foreach (var result in _results)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                result.Selected = EditorGUILayout.ToggleLeft(result.AssetPath, result.Selected);
                if (GUILayout.Button("選択", GUILayout.Width(40)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(result.AssetPath);
                    if (obj != null)
                    {
                        EditorGUIUtility.PingObject(obj);
                    }
                }

                EditorGUILayout.EndHorizontal();

                foreach (var issue in result.Issues)
                {
                    EditorGUILayout.LabelField($"  {issue}", EditorStyles.miniLabel);
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            var selectedCount = _results.Count(r => r.Selected);
            if (selectedCount > 0)
            {
                var style = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUILayout.Button($"選択した {selectedCount} 件を一括修正", style, GUILayout.Height(30)))
                {
                    ApplyFixes(_results.Where(r => r.Selected).ToList());
                }

                GUI.backgroundColor = Color.white;
            }
        }

        private IEnumerator AnalyzeCoroutine()
        {
            SetProgress("テクスチャを検索中...", 0f);
            yield return null;

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
            var results = new List<TextureResult>();

            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                SetProgress($"テクスチャ解析中... ({i + 1}/{guids.Length})", (float)i / guids.Length);

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                var issues = new List<string>();
                var recommended = new TextureRecommendation();

                // 元テクスチャサイズを取得
                importer.GetSourceTextureWidthAndHeight(out var srcWidth, out var srcHeight);
                var maxDim = Mathf.Max(srcWidth, srcHeight);

                // MaxSize チェック
                if (maxDim > 0 && importer.maxTextureSize > maxDim)
                {
                    var optimalMaxSize = NextPowerOfTwo(maxDim);
                    if (optimalMaxSize < importer.maxTextureSize)
                    {
                        issues.Add($"MaxSize: {importer.maxTextureSize} → {optimalMaxSize} (元画像: {srcWidth}x{srcHeight})");
                        recommended.MaxSize = optimalMaxSize;
                    }
                }

                // 圧縮チェック
                if (importer.textureCompression == TextureImporterCompression.Uncompressed)
                {
                    issues.Add($"圧縮: なし → Compressed 推奨");
                    recommended.Compression = TextureImporterCompression.Compressed;
                }

                // ミップマップチェック (UI/Sprite テクスチャ)
                if (importer.mipmapEnabled &&
                    (importer.textureType == TextureImporterType.Sprite ||
                     importer.textureType == TextureImporterType.GUI ||
                     path.Contains("/UI/")))
                {
                    issues.Add("ミップマップ: ON → OFF 推奨 (UI/Sprite)");
                    recommended.DisableMipmap = true;
                }

                if (issues.Count > 0)
                {
                    results.Add(new TextureResult
                    {
                        AssetPath = path,
                        Issues = issues,
                        Recommendation = recommended,
                        Selected = false,
                    });
                }

                if (i % 10 == 0)
                {
                    yield return null;
                }
            }

            _results = results;
            SetProgress("完了", 1f);
        }

        private void ApplyFixes(List<TextureResult> targets)
        {
            if (!EditorUtility.DisplayDialog(
                    "テクスチャ設定の一括修正",
                    $"{targets.Count} 件のテクスチャ設定を推奨値に変更します。続行しますか？",
                    "修正する", "キャンセル"))
            {
                return;
            }

            var count = 0;
            foreach (var target in targets)
            {
                var importer = AssetImporter.GetAtPath(target.AssetPath) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                var rec = target.Recommendation;
                if (rec.MaxSize.HasValue)
                {
                    importer.maxTextureSize = rec.MaxSize.Value;
                }

                if (rec.Compression.HasValue)
                {
                    importer.textureCompression = rec.Compression.Value;
                }

                if (rec.DisableMipmap)
                {
                    importer.mipmapEnabled = false;
                }

                importer.SaveAndReimport();
                count++;
            }

            Debug.Log($"[MornOptimizer] {count} 件のテクスチャ設定を修正しました。");
            _results = null;
        }

        private int NextPowerOfTwo(int v)
        {
            var p = 1;
            while (p < v)
            {
                p *= 2;
            }

            return p;
        }

        private class TextureResult
        {
            public string AssetPath;
            public List<string> Issues;
            public TextureRecommendation Recommendation;
            public bool Selected;
        }

        private class TextureRecommendation
        {
            public int? MaxSize;
            public TextureImporterCompression? Compression;
            public bool DisableMipmap;
        }
    }
}
