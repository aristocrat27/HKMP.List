using Modding;
using UnityEngine;

namespace HKMP.List.UI
{
    public sealed class ListCursorHook : MonoBehaviour
    {
        private static ListCursorHook _instance;
        private static bool _listVisible;
        private static bool _hasSavedCursorState;
        private static bool _savedCursorVisible;
        private static CursorLockMode _savedCursorLockState;

        private bool _subscribed;
        private int _chatHookRetryFrames;

        private void Update()
        {
            if (!_listVisible)
            {
                _chatHookRetryFrames = 0;
                return;
            }

            ListChatKeybindHook.EnforceChatClosedWhileVisible();

            if (ListChatKeybindHook.IsInstalled)
            {
                _chatHookRetryFrames = 0;
                return;
            }

            _chatHookRetryFrames++;
            if (_chatHookRetryFrames >= 30)
            {
                _chatHookRetryFrames = 0;
                ListChatKeybindHook.Install();
            }
        }

        public static bool IsListVisible
        {
            get { return _listVisible; }
        }

        public static void SetListVisible(bool visible)
        {
            if (_listVisible == visible)
            {
                if (visible)
                {
                    ListChatKeybindHook.OnListVisibilityChanged(true);
                    if (_instance != null)
                    {
                        _instance.BeginCursorOverride();
                    }
                    else
                    {
                        ForceCursorVisible();
                    }
                }

                return;
            }

            if (visible)
            {
                _savedCursorVisible = Cursor.visible;
                _savedCursorLockState = Cursor.lockState;
                _hasSavedCursorState = true;
                _listVisible = true;
                ListChatKeybindHook.OnListVisibilityChanged(true);

                if (_instance != null)
                {
                    _instance.BeginCursorOverride();
                }
                else
                {
                    ForceCursorVisible();
                }

                return;
            }

            _listVisible = false;
            ListChatKeybindHook.OnListVisibilityChanged(false);

            if (_instance != null)
            {
                _instance.EndCursorOverride();
            }

            RestoreSavedCursorState();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;

            if (_listVisible)
            {
                BeginCursorOverride();
            }
        }

        private void OnEnable()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;

            if (_listVisible)
            {
                BeginCursorOverride();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _listVisible)
            {
                ForceCursorVisible();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused && _listVisible)
            {
                ForceCursorVisible();
            }
        }

        private void OnDisable()
        {
            if (_instance != this)
            {
                return;
            }

            if (_listVisible)
            {
                SetListVisible(false);
            }
            else
            {
                EndCursorOverride();
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                SetListVisible(false);
                EndCursorOverride();
                _instance = null;
            }
        }

        private void BeginCursorOverride()
        {
            if (!_subscribed)
            {
                ModHooks.CursorHook -= ApplyCursorPolicy;
                ModHooks.CursorHook += ApplyCursorPolicy;
                _subscribed = true;
            }

            ForceCursorVisible();
        }

        private void EndCursorOverride()
        {
            if (!_subscribed)
            {
                return;
            }

            ModHooks.CursorHook -= ApplyCursorPolicy;
            _subscribed = false;
        }

        private static void ApplyCursorPolicy()
        {
            if (!_listVisible)
            {
                return;
            }

            ForceCursorVisible();
        }

        private static void ForceCursorVisible()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private static void RestoreSavedCursorState()
        {
            if (!_hasSavedCursorState)
            {
                return;
            }

            Cursor.lockState = _savedCursorLockState;
            Cursor.visible = _savedCursorVisible;

            _hasSavedCursorState = false;
        }
    }
}
