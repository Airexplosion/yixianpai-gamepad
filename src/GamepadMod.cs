using UnityEngine;
using UnityEngine.UI;
using Yx.ModSdk;
using Yx.ModSdk.Unity;

namespace YxGamepad
{
    /// <summary>
    /// 手柄适配 mod 入口。输入由 frida agent 在进程内读手柄、经 <see cref="PadBridge.Feed"/> 每帧喂入；
    /// 每帧驱动 <see cref="Gamepad"/> 做空间导航 + 牌位动作。另在游戏「设置」面板底部注入「手柄设置」按钮，
    /// 点开 <see cref="GamepadPanel"/> 看当前手柄 / 实时输入 / 改键。
    /// </summary>
    public sealed class GamepadMod : YxMod
    {
        UiKit _ui;
        GamepadPanel _panel;
        UiButton _settingsBtn;   // 注入设置面板底部的「手柄设置」按钮
        int _injectCd;

        public override void OnLoad(ModContext ctx)
        {
            Gamepad.SetLogger(ctx.Log.Info);
            GamepadApi.SetLogger(ctx.Log.Info);
            Gamepad.Init(ctx);
            _ui = new UiKit(ctx);
            _panel = new GamepadPanel(_ui, ctx);
            ctx.Log.Info(ctx.T("手柄适配已加载：设置面板底部有「手柄设置」按钮，可看状态 / 改键。", "Gamepad support loaded: a \"Gamepad Settings\" button at the bottom of the settings panel shows status / rebinding."));
        }

        public override void OnUpdate()
        {
            bool panelOpen = _panel != null && _panel.IsOpen;
            Gamepad.SetPad(PadBridge.Latest);
            Gamepad.SetPanelOpen(panelOpen);   // 面板开着 → Tick 挂起游戏导航，手柄交给面板自己
            Gamepad.Tick();
            InjectSettingsButton();
            if (panelOpen) { _panel.HandleNav(); _panel.Update(); }
        }

        // 设置面板显示时，往它底部挂「手柄设置」按钮（子物体，随面板显示/隐藏）。已挂好就不重复。
        void InjectSettingsButton()
        {
            if (_ui == null) return;
            if (_settingsBtn != null && _settingsBtn.IsAlive) return;
            if (_injectCd++ % 15 != 0) return;
            RectTransform root = GamepadApi.SettingsPanelRoot();
            if (root == null) return;
            Button tmpl = NativeUi.FindButton(root, null);   // 克隆设置面板里一个原生按钮 → 原生外观
            if (tmpl == null) return;                          // 面板里还没按钮，下次再试
            _settingsBtn = NativeUi.CloneButton(Context, tmpl, root, Context.T("手柄设置", "Gamepad Settings"), Context.T("打开手柄设置", "Open gamepad settings"), OpenPanel);
            if (_settingsBtn == null || !_settingsBtn.IsAlive) { _settingsBtn = null; return; }
            // 锚到设置面板底部居中，不依赖面板尺寸。
            var brt = _settingsBtn.GameObject.GetComponent(typeof(RectTransform)) as RectTransform;
            if (brt != null)
            {
                var bottom = new Vector2(0.5f, 0f);
                brt.anchorMin = bottom; brt.anchorMax = bottom; brt.pivot = bottom;
                brt.anchoredPosition = new Vector2(0f, 14f);
            }
        }

        void OpenPanel() { if (_panel != null) _panel.Open(); }

        public override void OnDisable()
        {
            Gamepad.Shutdown();
            if (_panel != null) _panel.Close();
            if (_settingsBtn != null && _settingsBtn.IsAlive) UnityEngine.Object.Destroy(_settingsBtn.GameObject);
            _settingsBtn = null;
            if (_ui != null) _ui.DestroyAll();
        }
    }
}
