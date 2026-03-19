using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MornLib
{
    /// <summary>
    /// PNG画像の透明余白をトリミングし、幅・高さを4の倍数に調整するタブ。
    /// 余白を削って4の倍数にならない場合は、最小限パディングして4の倍数にする。
    /// </summary>
    public sealed class MornOptimizerTrimTab : MornOptimizerTabBase
    {
        private List<TrimResult> _results;
        private bool _selectAll;
        private int _alphaThreshold;

        public MornOptimizerTrimTab(EditorWindow owner) : base(owner)
        {
            _alphaThreshold = 0;
        }

        public override string TabName => "余白トリム";

        protected override void DrawContent()
        {
            _alphaThreshold = EditorGUILayout.IntSlider("透明判定しきい値 (alpha ≤)", _alphaThreshold, 0, 16);

            if (GUILayout.Button("スキャン開始", GUILayout.Height(30)))
            {
                _results = null;
                _selectAll = false;
                StartAnalysis(AnalyzeCoroutine());
            }

            if (_results == null)
            {
                EditorGUILayout.HelpBox(
                    "PNG画像の透明余白を検出し、4の倍数サイズにトリミングします。\n" +
                    "削って4の倍数にできない場合は最小限パディングします。",
                    MessageType.Info);
                return;
            }

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox("トリミング可能な画像はありませんでした。", MessageType.Info);
                return;
            }

            // サマリー
            var totalSaved = _results.Sum(r => r.OriginalFileSize - r.EstimatedFileSize);
            EditorGUILayout.LabelField(
                $"トリミング可能: {_results.Count} 件 (推定削減: {FormatBytes(totalSaved)})",
                EditorStyles.boldLabel);
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

                var sizeInfo =
                    $"  {result.OriginalWidth}x{result.OriginalHeight} → {result.NewWidth}x{result.NewHeight}";
                var savings = result.OriginalFileSize - result.EstimatedFileSize;
                sizeInfo += $"  (ピクセル: {FormatPercent(result.PixelReduction)}減, ファイル推定: -{FormatBytes(savings)})";
                EditorGUILayout.LabelField(sizeInfo, EditorStyles.miniLabel);

                var detail = $"  コンテンツ領域: {result.ContentWidth}x{result.ContentHeight}";
                if (result.PaddedWidth > 0 || result.PaddedHeight > 0)
                {
                    detail += $"  パディング: +{result.PaddedWidth}w, +{result.PaddedHeight}h";
                }

                EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            var selectedCount = _results.Count(r => r.Selected);
            if (selectedCount > 0)
            {
                var selectedSaved = _results.Where(r => r.Selected).Sum(r => r.OriginalFileSize - r.EstimatedFileSize);
                var style = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                GUI.backgroundColor = new Color(1f, 0.6f, 0.3f);
                if (GUILayout.Button($"選択した {selectedCount} 件をトリミング (推定 -{FormatBytes(selectedSaved)})",
                        style, GUILayout.Height(30)))
                {
                    ApplyTrim(_results.Where(r => r.Selected).ToList());
                }

                GUI.backgroundColor = Color.white;
            }
        }

        private IEnumerator AnalyzeCoroutine()
        {
            SetProgress("PNG画像を検索中...", 0f);
            yield return null;

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/_Develop" });
            var results = new List<TrimResult>();

            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);

                if (i % 5 == 0)
                {
                    SetProgress($"解析中... ({i + 1}/{guids.Length}) {Path.GetFileName(path)}",
                        (float)i / guids.Length);
                    yield return null;
                }

                // PNG のみ対象
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = AnalyzeTexture(path);
                if (result != null)
                {
                    results.Add(result);
                }
            }

            // 削減量が大きい順
            results.Sort((a, b) => b.PixelReduction.CompareTo(a.PixelReduction));

            _results = results;

            Debug.Log($"[MornOptimizer] 余白トリム: {results.Count} 件のトリミング可能画像を検出");
            SetProgress("完了", 1f);
        }

        private TrimResult AnalyzeTexture(string assetPath)
        {
            var fullPath = Path.Combine(Application.dataPath, "..", assetPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            // 元ファイルからピクセルデータを直接読み込む (インポート設定に依存しない)
            var fileBytes = File.ReadAllBytes(fullPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(fileBytes))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }

            var width = tex.width;
            var height = tex.height;
            var pixels = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);

            // コンテンツ領域 (非透明ピクセルの bounding box) を検出
            var minX = width;
            var maxX = -1;
            var minY = height;
            var maxY = -1;
            var threshold = (byte)_alphaThreshold;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[y * width + x].a <= threshold)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            // 完全に透明な画像はスキップ
            if (maxX < 0)
            {
                return null;
            }

            var contentW = maxX - minX + 1;
            var contentH = maxY - minY + 1;

            // 4の倍数に調整: まず切り下げ、コンテンツが収まらなければ切り上げ
            var newW = FloorTo4(contentW);
            if (newW < contentW)
            {
                newW = CeilTo4(contentW);
            }

            var newH = FloorTo4(contentH);
            if (newH < contentH)
            {
                newH = CeilTo4(contentH);
            }

            // 元サイズと同じかそれ以上なら意味なし
            if (newW >= width && newH >= height)
            {
                return null;
            }

            // 片方だけ大きくなる場合でも、総ピクセル数が減れば意味あり
            var originalPixels = (long)width * height;
            var newPixels = (long)newW * newH;
            if (newPixels >= originalPixels)
            {
                return null;
            }

            var paddedW = newW - contentW;
            var paddedH = newH - contentH;

            // ファイルサイズ推定 (ピクセル比率で概算)
            var fileSize = fileBytes.Length;
            var ratio = (double)newPixels / originalPixels;
            var estimatedFileSize = (long)(fileSize * ratio);

            return new TrimResult
            {
                AssetPath = assetPath,
                OriginalWidth = width,
                OriginalHeight = height,
                ContentWidth = contentW,
                ContentHeight = contentH,
                NewWidth = newW,
                NewHeight = newH,
                PaddedWidth = paddedW,
                PaddedHeight = paddedH,
                ContentMinX = minX,
                ContentMinY = minY,
                OriginalFileSize = fileSize,
                EstimatedFileSize = estimatedFileSize,
                PixelReduction = 1.0 - ratio,
                Selected = false,
            };
        }

        private void ApplyTrim(List<TrimResult> targets)
        {
            if (!EditorUtility.DisplayDialog(
                    "画像トリミング確認",
                    $"{targets.Count} 件の PNG をトリミングします。\n元画像は上書きされます。続行しますか？",
                    "トリミング実行", "キャンセル"))
            {
                return;
            }

            var count = 0;
            foreach (var target in targets)
            {
                try
                {
                    TrimTexture(target);
                    count++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MornOptimizer] {target.AssetPath} のトリミングに失敗: {e.Message}");
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[MornOptimizer] {count} 件の画像をトリミングしました。");
            _results = null;
        }

        private void TrimTexture(TrimResult target)
        {
            var fullPath = Path.Combine(Application.dataPath, "..", target.AssetPath);
            var fileBytes = File.ReadAllBytes(fullPath);
            var srcTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!srcTex.LoadImage(fileBytes))
            {
                UnityEngine.Object.DestroyImmediate(srcTex);
                return;
            }

            var srcPixels = srcTex.GetPixels32();
            var srcW = srcTex.width;
            UnityEngine.Object.DestroyImmediate(srcTex);

            // 新テクスチャにコンテンツ領域をコピー (センタリング)
            var dstTex = new Texture2D(target.NewWidth, target.NewHeight, TextureFormat.RGBA32, false);
            var dstPixels = new Color32[target.NewWidth * target.NewHeight];

            // 透明で初期化
            var transparent = new Color32(0, 0, 0, 0);
            for (var i = 0; i < dstPixels.Length; i++)
            {
                dstPixels[i] = transparent;
            }

            // コンテンツをパディング分だけオフセットして配置 (左下基準で均等配分)
            var offsetX = target.PaddedWidth / 2;
            var offsetY = target.PaddedHeight / 2;

            for (var y = 0; y < target.ContentHeight; y++)
            {
                for (var x = 0; x < target.ContentWidth; x++)
                {
                    var srcIdx = (target.ContentMinY + y) * srcW + (target.ContentMinX + x);
                    var dstIdx = (offsetY + y) * target.NewWidth + (offsetX + x);
                    dstPixels[dstIdx] = srcPixels[srcIdx];
                }
            }

            dstTex.SetPixels32(dstPixels);
            dstTex.Apply();

            var pngBytes = dstTex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(dstTex);

            File.WriteAllBytes(fullPath, pngBytes);
        }

        private static int FloorTo4(int v)
        {
            return v / 4 * 4;
        }

        private static int CeilTo4(int v)
        {
            return (v + 3) / 4 * 4;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            return bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        private static string FormatPercent(double ratio)
        {
            return $"{ratio * 100:F1}%";
        }

        private class TrimResult
        {
            public string AssetPath;
            public int OriginalWidth;
            public int OriginalHeight;
            public int ContentWidth;
            public int ContentHeight;
            public int NewWidth;
            public int NewHeight;
            public int PaddedWidth;
            public int PaddedHeight;
            public int ContentMinX;
            public int ContentMinY;
            public long OriginalFileSize;
            public long EstimatedFileSize;
            public double PixelReduction;
            public bool Selected;
        }
    }
}
