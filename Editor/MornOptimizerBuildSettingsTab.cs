using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MornLib
{
    public sealed class MornOptimizerBuildSettingsTab : MornOptimizerTabBase
    {
        private List<SettingCheck> _checks;

        public MornOptimizerBuildSettingsTab(EditorWindow owner) : base(owner)
        {
        }

        public override string TabName => "ビルド設定";

        protected override void DrawContent()
        {
            if (GUILayout.Button("設定を確認", GUILayout.Height(30)))
            {
                _checks = AnalyzeSettings();
            }

            if (_checks == null)
            {
                EditorGUILayout.HelpBox("「設定を確認」で現在のビルド設定を推奨値と比較します。", MessageType.Info);
                return;
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            EditorGUILayout.LabelField($"ビルドターゲット: {target}", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            ScrollPos = EditorGUILayout.BeginScrollView(ScrollPos);

            var hasIssue = false;
            foreach (var check in _checks)
            {
                var bgColor = GUI.backgroundColor;
                if (!check.IsOptimal)
                {
                    GUI.backgroundColor = new Color(1f, 0.95f, 0.7f);
                    hasIssue = true;
                }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                var icon = check.IsOptimal ? "\u2714" : "\u26a0";
                EditorGUILayout.LabelField($"{icon} {check.SettingName}", EditorStyles.boldLabel, GUILayout.Width(250));
                EditorGUILayout.LabelField($"現在: {check.CurrentValue}", GUILayout.Width(200));

                if (!check.IsOptimal)
                {
                    EditorGUILayout.LabelField($"推奨: {check.RecommendedValue}", GUILayout.Width(200));
                    if (check.ApplyAction != null && GUILayout.Button("修正", GUILayout.Width(50)))
                    {
                        check.ApplyAction();
                        _checks = AnalyzeSettings();
                        Repaint();
                    }
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();

                GUI.backgroundColor = bgColor;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            if (hasIssue)
            {
                var style = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                if (GUILayout.Button("全て推奨設定に変更", style, GUILayout.Height(30)))
                {
                    foreach (var check in _checks)
                    {
                        if (!check.IsOptimal)
                        {
                            check.ApplyAction?.Invoke();
                        }
                    }

                    _checks = AnalyzeSettings();
                    Debug.Log("[MornOptimizer] ビルド設定を推奨値に変更しました。");
                }

                GUI.backgroundColor = Color.white;
            }
            else
            {
                EditorGUILayout.HelpBox("全てのビルド設定が推奨値です。", MessageType.Info);
            }
        }

        private List<SettingCheck> AnalyzeSettings()
        {
            var checks = new List<SettingCheck>();
            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);

            // Scripting Backend
            var backend = PlayerSettings.GetScriptingBackend(namedTarget);
            checks.Add(new SettingCheck
            {
                SettingName = "Scripting Backend",
                CurrentValue = backend.ToString(),
                RecommendedValue = "IL2CPP",
                IsOptimal = backend == ScriptingImplementation.IL2CPP,
                ApplyAction = () => PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP),
            });

            // Managed Stripping Level
            var stripping = PlayerSettings.GetManagedStrippingLevel(namedTarget);
            var recommendedStripping = buildTarget == BuildTarget.WebGL
                ? ManagedStrippingLevel.High
                : ManagedStrippingLevel.Medium;
            checks.Add(new SettingCheck
            {
                SettingName = "Managed Stripping Level",
                CurrentValue = stripping.ToString(),
                RecommendedValue = recommendedStripping.ToString(),
                IsOptimal = stripping >= recommendedStripping,
                ApplyAction = () => PlayerSettings.SetManagedStrippingLevel(namedTarget, recommendedStripping),
            });

            // Development Build
            checks.Add(new SettingCheck
            {
                SettingName = "Development Build",
                CurrentValue = EditorUserBuildSettings.development ? "ON" : "OFF",
                RecommendedValue = "OFF",
                IsOptimal = !EditorUserBuildSettings.development,
                ApplyAction = () => EditorUserBuildSettings.development = false,
            });

            // WebGL 固有設定
            if (buildTarget == BuildTarget.WebGL)
            {
                var compression = PlayerSettings.WebGL.compressionFormat;
                checks.Add(new SettingCheck
                {
                    SettingName = "WebGL Compression",
                    CurrentValue = compression.ToString(),
                    RecommendedValue = "Brotli",
                    IsOptimal = compression == WebGLCompressionFormat.Brotli,
                    ApplyAction = () => PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli,
                });
            }

            return checks;
        }

        private class SettingCheck
        {
            public string SettingName;
            public string CurrentValue;
            public string RecommendedValue;
            public bool IsOptimal;
            public Action ApplyAction;
        }
    }
}
