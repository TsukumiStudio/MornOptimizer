using UnityEditor;
using UnityEngine;

namespace MornLib
{
    public sealed class MornOptimizerWindow : EditorWindow
    {
        private MornOptimizerTabBase[] _tabs;
        private string[] _tabNames;
        private int _selectedTab;

        [MenuItem("Tools/MornOptimizer")]
        private static void Open()
        {
            GetWindow<MornOptimizerWindow>("Morn Optimizer");
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

            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames, GUILayout.Height(25));
            EditorGUILayout.Space();

            _tabs[_selectedTab].OnGUI();
        }
    }
}
