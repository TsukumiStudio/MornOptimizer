using UnityEditor;
using UnityEngine;

namespace MornLib
{
    public sealed class MornOptimizerWindow : EditorWindow
    {
        private const string GitUrl = "https://github.com/TsukumiStudio/MornOptimizer";

        private MornOptimizerTabBase[] _tabs;
        private string[] _tabNames;
        private int _selectedTab;
        private string _version;
        private Texture2D _icon;

        [MenuItem("Tools/MornOptimizer")]
        private static void Open()
        {
            var window = GetWindow<MornOptimizerWindow>();
            window.SetTitleWithIcon();
        }

        private void OnEnable()
        {
            _tabs = new MornOptimizerTabBase[]
            {
                new MornOptimizerPackageTab(this),
                new MornOptimizerAsmdefTab(this),
                new MornOptimizerTextureTab(this),
                new MornOptimizerTrimTab(this),
                new MornOptimizerUnreferencedTab(this),
                new MornOptimizerBuildSettingsTab(this),
            };
            _tabNames = new string[_tabs.Length];
            for (var i = 0; i < _tabs.Length; i++)
            {
                _tabNames[i] = _tabs[i].TabName;
            }

            // アイコン読み込み
            var iconGuids = AssetDatabase.FindAssets("MornOptimizer_Icon t:texture2d");
            foreach (var guid in iconGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("MornOptimizer_Icon.png"))
                {
                    _icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    break;
                }
            }

            SetTitleWithIcon();

            // package.jsonからバージョン取得
            _version = "unknown";
            var packageGuids = AssetDatabase.FindAssets("package t:textasset");
            foreach (var guid in packageGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("MornOptimizer") && path.EndsWith("package.json"))
                {
                    var json = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    if (json != null)
                    {
                        var packageInfo = JsonUtility.FromJson<PackageInfo>(json.text);
                        _version = packageInfo.version;
                    }
                    break;
                }
            }
        }

        private void OnDisable()
        {
            if (_tabs == null)
            {
                return;
            }

            foreach (var tab in _tabs)
            {
                tab.OnDisable();
            }
        }

        private void OnGUI()
        {
            if (_tabs == null)
            {
                OnEnable();
            }

            DrawHeader();
            EditorGUILayout.Space();

            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames, GUILayout.Height(25));
            EditorGUILayout.Space();

            _tabs[_selectedTab].OnGUI();
        }

        private void SetTitleWithIcon()
        {
            titleContent = _icon != null
                ? new GUIContent(_icon)
                : new GUIContent("MornOptimizer");
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"MornOptimizer v{_version}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("GitHub", EditorStyles.linkLabel))
                {
                    Application.OpenURL(GitUrl);
                }
            }
        }

        [System.Serializable]
        private struct PackageInfo
        {
            public string version;
        }
    }
}
