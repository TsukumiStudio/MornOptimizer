using System.Collections;
using UnityEditor;
using UnityEngine;

namespace MornLib
{
    public abstract class MornOptimizerTabBase
    {
        private readonly EditorWindow _owner;
        private IEnumerator _coroutine;
        private string _progressPhase;
        private float _progressValue;

        protected Vector2 ScrollPos;

        protected MornOptimizerTabBase(EditorWindow owner)
        {
            _owner = owner;
        }

        public abstract string TabName { get; }
        public bool IsAnalyzing => _coroutine != null;

        public void OnGUI()
        {
            if (IsAnalyzing)
            {
                var rect = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(rect, _progressValue, _progressPhase ?? "解析中...");
                EditorGUILayout.Space();

                if (GUILayout.Button("キャンセル"))
                {
                    StopAnalysis();
                }

                return;
            }

            DrawContent();
        }

        public void OnDisable()
        {
            StopAnalysis();
        }

        protected abstract void DrawContent();

        protected void StartAnalysis(IEnumerator coroutine)
        {
            StopAnalysis();
            _coroutine = coroutine;
            EditorApplication.update += TickAnalysis;
        }

        protected void StopAnalysis()
        {
            if (_coroutine == null)
            {
                return;
            }

            EditorApplication.update -= TickAnalysis;
            _coroutine = null;
            _progressPhase = null;
            _progressValue = 0f;
            _owner.Repaint();
        }

        protected void SetProgress(string phase, float value)
        {
            _progressPhase = phase;
            _progressValue = Mathf.Clamp01(value);
        }

        protected void Repaint()
        {
            _owner.Repaint();
        }

        private void TickAnalysis()
        {
            if (_coroutine == null || !_coroutine.MoveNext())
            {
                StopAnalysis();
            }

            _owner.Repaint();
        }
    }
}
