using System;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Yx.ModSdk;
using Yx.ModSdk.Unity;

namespace YxGamepad
{
    /// <summary>
    /// 手柄设置面板（自绘叠加，用 SDK 的 UiKit）。上部一张手柄示意图（按键实时高亮），下部动作列表（可改键）。
    /// 纯 UGUI，不碰游戏类型；数据来自 <see cref="PadBridge"/>（手柄名/来源）与 <see cref="Gamepad"/>（动作/绑定/实时快照/改键）。
    /// 用鼠标操作；手柄只在「改键捕获」时用来按下要绑的键。只在主线程用。
    /// </summary>
    public sealed class GamepadPanel
    {
        static readonly Vector2 PanelSize = new Vector2(720f, 680f);
        static readonly Color BgColor = new Color(0.06f, 0.05f, 0.04f, 0.97f);
        static readonly Color Normal = new Color(0.90f, 0.86f, 0.74f, 1f);
        static readonly Color Active = new Color(0.45f, 0.92f, 0.52f, 1f);
        static readonly Color Prompt = new Color(1f, 0.85f, 0.32f, 1f);
        static readonly Color Dim = new Color(0.6f, 0.56f, 0.48f, 1f);
        static readonly Color PartIdle = new Color(0.28f, 0.26f, 0.22f, 1f);
        static readonly Color PartOn = new Color(0.45f, 0.92f, 0.52f, 1f);
        static readonly Color BodyColor = new Color(0.12f, 0.11f, 0.09f, 1f);

        readonly UiKit _ui;
        GameObject _root;
        TextMeshProUGUI _pad;
        TextMeshProUGUI _raw;
        TextMeshProUGUI[] _name;
        TextMeshProUGUI[] _bind;
        UiButton[] _rebind;
        UiButton _close;
        int _count;

        // 手柄导航面板本身：焦点列表（动作下标，-1=关闭按钮）+ 高亮框 + 上一帧输入（做边沿检测）。
        int[] _focusAct;
        int _focusIdx;
        Image _hi;
        Gamepad.Snapshot _prevSnap;
        static readonly Color HiColor = new Color(0.45f, 0.92f, 0.52f, 0.30f);

        // 示意图元素
        Image _lsDot, _rsDot;
        Image[] _face = new Image[4];
        TextMeshProUGUI[] _faceLabel = new TextMeshProUGUI[4];
        Image _lb, _rb, _lt, _rt, _back, _start;
        Image _dUp, _dDown, _dLeft, _dRight;
        Vector2 _lsC, _rsC;   // 摇杆中心（画布坐标）
        string _srcShown = "";

        public GamepadPanel(UiKit ui) { _ui = ui; }

        public bool IsOpen { get { return _root != null; } }

        public void Toggle() { if (_root != null) Close(); else Open(); }

        public void Open()
        {
            if (_root != null) return;
            var center = new Vector2(0.5f, 0.5f);
            _root = _ui.Panel("GP_PadPanel", center, center, Vector2.zero, PanelSize, BgColor, true);
            if (_root == null) return;
            var cv = _root.AddComponent(typeof(Canvas)) as Canvas;
            cv.overrideSorting = true; cv.sortingOrder = 32000;
            _root.AddComponent(typeof(GraphicRaycaster));

            Transform p = _root.transform;
            _ui.Label(p, "title", "手柄设置（手柄：方向选择 / 确认键激活 / 取消键关闭）", new Vector2(16f, -6f), new Vector2(560f, 34f), 20f);
            _close = _ui.TextButton(p, "close", "关闭", new Vector2(PanelSize.x - 92f, -8f), new Vector2(76f, 32f), Close);
            _pad = _ui.Label(p, "pad", "", new Vector2(16f, -48f), new Vector2(PanelSize.x - 32f, 26f), 18f);
            _raw = _ui.Label(p, "raw", "", new Vector2(16f, -76f), new Vector2(PanelSize.x - 32f, 24f), 15f);
            _raw.color = Dim;

            BuildDiagram(p);
            BuildList(p);
            BuildFocus();
            // 焦点高亮框（半透明绿，盖在当前焦点按钮上；不挡射线）。最后建 → 渲染在最上层。
            _hi = Box(p, "GP_focusHi", 0f, 0f, 10f, 10f, HiColor);
            _hi.transform.SetAsLastSibling();
            _prevSnap = Gamepad.Capture();
            Update();
        }

        // 焦点顺序：可改键的动作（有「改键」按钮的）按序，最后一个是「关闭」。
        void BuildFocus()
        {
            int n = 0;
            for (int i = 0; i < _count; i++) if (_rebind[i] != null) n++;
            _focusAct = new int[n + 1];
            int k = 0;
            for (int i = 0; i < _count; i++) if (_rebind[i] != null) _focusAct[k++] = i;
            _focusAct[k] = -1;   // 关闭
            _focusIdx = 0;
        }

        public void Close()
        {
            if (_root == null) return;
            Gamepad.CancelRebind();
            _ui.Destroy(_root);
            _root = null; _pad = null; _raw = null; _name = null; _bind = null; _rebind = null; _close = null;
            _focusAct = null; _hi = null;
            _lsDot = null; _rsDot = null; _lb = null; _rb = null; _lt = null; _rt = null; _back = null; _start = null;
            _dUp = null; _dDown = null; _dLeft = null; _dRight = null; _srcShown = "";
        }

        void OnRebind(int i)
        {
            if (Gamepad.IsRebinding && Gamepad.RebindingIndex == i) Gamepad.CancelRebind();
            else Gamepad.BeginRebind(i);
        }

        /// <summary>用手柄导航这个面板本身：方向键选、确认键激活、取消键关闭。每帧调（面板开着时）。</summary>
        public void HandleNav()
        {
            if (_root == null || _focusAct == null || _focusAct.Length == 0) return;
            Gamepad.Snapshot cur = Gamepad.Capture();
            // 改键捕获中：手柄的下一个按下要被 Gamepad 抓去当绑定，这里什么都不做（也别拦，交给捕获）。
            if (Gamepad.IsRebinding) { _prevSnap = cur; return; }
            bool up = Edge(Gamepad.Act.NavUp, cur), down = Edge(Gamepad.Act.NavDown, cur);
            bool ok = Edge(Gamepad.Act.Confirm, cur), cancel = Edge(Gamepad.Act.Cancel, cur);
            _prevSnap = cur;
            if (cancel) { Close(); return; }
            if (up) MoveFocus(-1);
            else if (down) MoveFocus(1);
            if (ok) ActivateFocus();
        }

        bool Edge(Gamepad.Act a, Gamepad.Snapshot cur) { return Gamepad.ActiveAct(a, cur) && !Gamepad.ActiveAct(a, _prevSnap); }

        void MoveFocus(int d)
        {
            int n = _focusAct.Length;
            _focusIdx = ((_focusIdx + d) % n + n) % n;
        }

        void ActivateFocus()
        {
            if (_focusAct == null || _focusIdx < 0 || _focusIdx >= _focusAct.Length) return;
            int a = _focusAct[_focusIdx];
            if (a < 0) Close();
            else OnRebind(a);
        }

        GameObject FocusedGo()
        {
            if (_focusAct == null || _focusIdx < 0 || _focusIdx >= _focusAct.Length) return null;
            int a = _focusAct[_focusIdx];
            if (a < 0) return (_close != null && _close.IsAlive) ? _close.GameObject : null;
            return (a < _count && _rebind[a] != null && _rebind[a].IsAlive) ? _rebind[a].GameObject : null;
        }

        // 把高亮框贴到当前焦点按钮上（同一父级，直接抄锚点/位置/尺寸）。
        void UpdateHighlight()
        {
            if (_hi == null) return;
            GameObject go = FocusedGo();
            var hrt = _hi.rectTransform;
            if (go == null) { _hi.enabled = false; return; }
            var brt = go.GetComponent(typeof(RectTransform)) as RectTransform;
            if (brt == null) { _hi.enabled = false; return; }
            _hi.enabled = true;
            hrt.anchorMin = brt.anchorMin; hrt.anchorMax = brt.anchorMax; hrt.pivot = brt.pivot;
            hrt.anchoredPosition = brt.anchoredPosition;
            hrt.sizeDelta = brt.sizeDelta;
            hrt.SetAsLastSibling();
        }

        // ── 示意图 ──────────────────────────────────────────────
        void BuildDiagram(Transform p)
        {
            // 机身背板
            Box(p, "body", 360f, -196f, 460f, 168f, BodyColor);
            // 扳机 + 肩键（左/右）
            _lt = Box(p, "LT", 175f, -96f, 78f, 16f, PartIdle);
            _lb = Box(p, "LB", 175f, -116f, 78f, 18f, PartIdle);
            _rt = Box(p, "RT", 545f, -96f, 78f, 16f, PartIdle);
            _rb = Box(p, "RB", 545f, -116f, 78f, 18f, PartIdle);
            // 左摇杆（外圈 + 可动内点）
            _lsC = new Vector2(175f, -180f);
            Box(p, "LSring", _lsC.x, _lsC.y, 58f, 58f, new Color(0.20f, 0.19f, 0.16f, 1f));
            _lsDot = Box(p, "LSdot", _lsC.x, _lsC.y, 24f, 24f, PartIdle);
            // 十字键（上下左右）
            float dx = 288f, dy = -196f;
            _dUp = Box(p, "Dup", dx, dy - 22f, 20f, 20f, PartIdle);
            _dDown = Box(p, "Ddn", dx, dy + 22f, 20f, 20f, PartIdle);
            _dLeft = Box(p, "Dlf", dx - 22f, dy, 20f, 20f, PartIdle);
            _dRight = Box(p, "Drt", dx + 22f, dy, 20f, 20f, PartIdle);
            // Back / Start（中间两个小键）
            _back = Box(p, "Back", 336f, -196f, 30f, 16f, PartIdle);
            _start = Box(p, "Start", 384f, -196f, 30f, 16f, PartIdle);
            // 右摇杆
            _rsC = new Vector2(430f, -220f);
            Box(p, "RSring", _rsC.x, _rsC.y, 58f, 58f, new Color(0.20f, 0.19f, 0.16f, 1f));
            _rsDot = Box(p, "RSdot", _rsC.x, _rsC.y, 24f, 24f, PartIdle);
            // 面板四键（钻石排列）：idx3=上 idx0=下 idx2=左 idx1=右
            float fx = 545f, fy = -180f;
            _face[3] = BoxLabeled(p, "F3", fx, fy - 24f, 28f, 28f, out _faceLabel[3]);
            _face[0] = BoxLabeled(p, "F0", fx, fy + 24f, 28f, 28f, out _faceLabel[0]);
            _face[2] = BoxLabeled(p, "F2", fx - 26f, fy, 28f, 28f, out _faceLabel[2]);
            _face[1] = BoxLabeled(p, "F1", fx + 26f, fy, 28f, 28f, out _faceLabel[1]);
            // 小标注
            _ui.Label(p, "lLS", "左摇杆", new Vector2(148f, -214f), new Vector2(60f, 18f), 12f).color = Dim;
            _ui.Label(p, "lRS", "右摇杆", new Vector2(404f, -254f), new Vector2(60f, 18f), 12f).color = Dim;
        }

        Image Box(Transform parent, string name, float cx, float cy, float w, float h, Color col)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent(typeof(RectTransform)) as RectTransform;
            var tl = new Vector2(0f, 1f);
            rt.anchorMin = tl; rt.anchorMax = tl; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cx, cy);
            rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent(typeof(Image)) as Image;
            img.color = col; img.raycastTarget = false;
            return img;
        }

        Image BoxLabeled(Transform parent, string name, float cx, float cy, float w, float h, out TextMeshProUGUI label)
        {
            Image img = Box(parent, name, cx, cy, w, h, PartIdle);
            var go = new GameObject("t");
            go.transform.SetParent(img.transform, false);
            label = go.AddComponent(typeof(TextMeshProUGUI)) as TextMeshProUGUI;
            TMP_FontAsset font = Ui.FindFont(); if (font != null) label.font = font;
            label.fontSize = 15f; label.color = new Color(0.1f, 0.1f, 0.08f, 1f);
            label.alignment = TextAlignmentOptions.Center; label.raycastTarget = false; label.enableWordWrapping = false;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return img;
        }

        void UpdateDiagram()
        {
            Gamepad.Snapshot snap = Gamepad.Capture();
            bool[] b = snap.buttons; float[] a = snap.axes;
            // 面板四键标注按来源切换（Xbox: A B X Y / DS4: × ○ □ △）
            string src = PadBridge.Src;
            if (src != _srcShown)
            {
                _srcShown = src;
                bool ds4 = src == "winmm";
                SetFace(0, ds4 ? "×" : "A"); SetFace(1, ds4 ? "○" : "B");
                SetFace(2, ds4 ? "□" : "X"); SetFace(3, ds4 ? "△" : "Y");
            }
            for (int i = 0; i < 4; i++) if (_face[i] != null) _face[i].color = Btn(b, i) ? PartOn : PartIdle;
            if (_lb != null) _lb.color = Btn(b, 4) ? PartOn : PartIdle;
            if (_rb != null) _rb.color = Btn(b, 5) ? PartOn : PartIdle;
            if (_back != null) _back.color = Btn(b, 6) ? PartOn : PartIdle;
            if (_start != null) _start.color = Btn(b, 7) ? PartOn : PartIdle;
            if (_lt != null) _lt.color = Ax(a, 4) > 0.5f ? PartOn : PartIdle;
            if (_rt != null) _rt.color = Ax(a, 5) > 0.5f ? PartOn : PartIdle;
            // 十字键
            if (_dUp != null) _dUp.color = Ax(a, 7) > 0.5f ? PartOn : PartIdle;
            if (_dDown != null) _dDown.color = Ax(a, 7) < -0.5f ? PartOn : PartIdle;
            if (_dLeft != null) _dLeft.color = Ax(a, 6) < -0.5f ? PartOn : PartIdle;
            if (_dRight != null) _dRight.color = Ax(a, 6) > 0.5f ? PartOn : PartIdle;
            // 摇杆内点：随轴移动 + 推动时高亮
            MoveStick(_lsDot, _lsC, Ax(a, 0), Ax(a, 1));
            MoveStick(_rsDot, _rsC, Ax(a, 2), Ax(a, 3));
        }

        void MoveStick(Image dot, Vector2 c, float x, float y)
        {
            if (dot == null) return;
            var rt = dot.rectTransform;
            rt.anchoredPosition = new Vector2(c.x + x * 16f, c.y + y * 16f);   // y 上=正 → 画布 y 上为负，故 +y*16 表示上移（c.y 已是负）
            dot.color = (x > 0.5f || x < -0.5f || y > 0.5f || y < -0.5f) ? PartOn : PartIdle;
        }

        void SetFace(int i, string t) { if (_faceLabel[i] != null) _faceLabel[i].text = t; }
        static bool Btn(bool[] b, int i) { return b != null && i >= 0 && i < b.Length && b[i]; }
        static float Ax(float[] a, int i) { return a != null && i >= 0 && i < a.Length ? a[i] : 0f; }

        // ── 动作列表 ────────────────────────────────────────────
        void BuildList(Transform p)
        {
            _count = Gamepad.ActionCount;
            _name = new TextMeshProUGUI[_count];
            _bind = new TextMeshProUGUI[_count];
            _rebind = new UiButton[_count];
            const float startY = -300f;
            const float rowH = 26f;
            int leftRow = 0, rightRow = 0;
            for (int i = 0; i < _count; i++)
            {
                bool canRebind = Gamepad.ActionRebindable(i);
                float x, y;
                if (canRebind) { x = 16f; y = startY - leftRow * rowH; leftRow++; }
                else { x = 420f; y = startY - rightRow * rowH; rightRow++; }
                _name[i] = _ui.Label(p, "n" + S(i), Gamepad.ActionName(i), new Vector2(x, y), new Vector2(canRebind ? 150f : 120f, 24f), 16f);
                float bx = x + (canRebind ? 150f : 124f);
                _bind[i] = _ui.Label(p, "b" + S(i), Gamepad.ActionBindingText(i), new Vector2(bx, y), new Vector2(150f, 24f), 16f);
                if (canRebind)
                {
                    int idx = i;
                    _rebind[i] = _ui.TextButton(p, "r" + S(i), "改键", new Vector2(bx + 152f, y + 1f), new Vector2(62f, 24f), () => OnRebind(idx));
                }
            }
        }

        /// <summary>面板开着时每帧调：刷新手柄名、示意图、各动作绑定/高亮/改键提示。</summary>
        public void Update()
        {
            if (_root == null) return;
            if (_pad != null)
            {
                if (PadBridge.Connected)
                    _pad.text = "当前手柄：" + PadBridge.Name + "（" + PadBridge.Src + " 槽" + PadBridge.SlotText + "）";
                else
                    _pad.text = "未检测到手柄（插上手柄、并确认没被 Steam 输入接管）";
            }
            if (_raw != null) _raw.text = "实时：" + RawText();
            UpdateDiagram();
            UpdateHighlight();

            bool rebinding = Gamepad.IsRebinding;
            int rIdx = Gamepad.RebindingIndex;
            for (int i = 0; i < _count; i++)
            {
                if (_bind[i] == null) continue;
                bool isTarget = rebinding && rIdx == i;
                if (isTarget) { _bind[i].text = "请按手柄上的键…"; _bind[i].color = Prompt; }
                else { _bind[i].text = Gamepad.ActionBindingText(i); _bind[i].color = Gamepad.ActionActive(i) ? Active : Normal; }
                if (_rebind[i] != null && _rebind[i].IsAlive) _rebind[i].SetText(isTarget ? "取消" : "改键");
            }
        }

        static string RawText()
        {
            Gamepad.Snapshot snap = Gamepad.Capture();
            var sb = new StringBuilder();
            if (snap.buttons != null)
                for (int b = 0; b < snap.buttons.Length; b++)
                    if (snap.buttons[b]) { if (sb.Length > 0) sb.Append(' '); sb.Append(Gamepad.ButtonLabel(b)); }
            if (snap.axes != null)
                for (int a = 0; a < snap.axes.Length; a++)
                {
                    float v = snap.axes[a];
                    if (v > 0.5f || v < -0.5f) { if (sb.Length > 0) sb.Append(' '); sb.Append(Gamepad.AxisLabel(a)).Append(v > 0 ? '+' : '-'); }
                }
            return sb.Length > 0 ? sb.ToString() : "（无）";
        }

        static string S(int i) { return i.ToString(CultureInfo.InvariantCulture); }
    }
}
