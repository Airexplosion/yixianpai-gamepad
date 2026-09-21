using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Yx.ModSdk;

namespace YxGamepad
{
    // 手柄兼容 — 空间选择导航。挂进 Hud32.PumpActions(60fps 主线程)。
    // 左摇杆=光标(推方向选该方向最近的目标:手牌/棋盘/场上按钮/弹窗选项);○=确认/点击。
    // 右摇杆=牌位动作(上摆下收左右换位);L1炼/R1换/L2合/□突破/△准备/×取消。
    public static class Gamepad
    {
        const bool DIAG = false;   // 诊断总开关:true=每帧 dump 目标/中心射线/按键到 %TEMP%\gp_probe.log;发布时 false
        // 诊断出口：接管理器日志（GamepadMod.OnLoad 经 SetLogger 传入 ctx.Log.Info）。DIAG=false 时整条链关闭。
        static Action<string> s_log;
        public static void SetLogger(Action<string> log) { s_log = log; }
        static void W(string m) { if (!DIAG) return; try { if (s_log != null) s_log(m); } catch (Exception) { } }
        static bool s_en;
        static string T(string zh, string en) { return s_en ? en : zh; }
        // 停用/热重载时收起自建 UI（准星、悬浮）。热键/订阅/任务由宿主自动清，这里只管自己 new 的对象。
        public static void Shutdown() { try { HideReticle(); SetHover(null); SetHoverGeneral(null); } catch (Exception) { } }

        // ── 输入快照 + 绑定 ────────────────────────────────────────
        // 全部输入由注入器侧 SDL 归一化后每帧喂入(见 pad/ 与实现计划头部契约)。
        public struct Snapshot { public bool[] buttons; public float[] axes; }
        const int NBTN = 11;   // A B X Y LB RB Back Start L3 R3 Guide
        const int NAX = 8;     // LX LY RX RY LT RT DpadX DpadY
        static readonly bool[] s_padBtn = new bool[NBTN];
        static readonly float[] s_padAx = new float[NAX];
        static int s_rsDbg;
        // 注入器每帧喂:"b0,…,b10,a0,…,a7" —— 11 个 0/1 + 8 个浮点。
        public static string SetPad(string s)
        {
            try { if (s_rsDbg++ % 60 == 0) W("pad recv " + s); } catch (Exception) { }
            try {
                var p = (s ?? "").Split(',');
                if (p.Length >= NBTN + NAX) {
                    for (int i = 0; i < NBTN; i++) s_padBtn[i] = (p[i] == "1");
                    for (int i = 0; i < NAX; i++) { float v; if (float.TryParse(p[NBTN + i], out v)) s_padAx[i] = v; }
                }
            } catch (Exception) { }
            return "ok";
        }
        public static Snapshot Capture()
        {
            var b = new bool[NBTN]; Array.Copy(s_padBtn, b, NBTN);
            var a = new float[NAX]; Array.Copy(s_padAx, a, NAX);
            return new Snapshot { buttons = b, axes = a };
        }
        public enum InKind { Button, Axis }
        public struct Spec { public InKind kind; public int idx; public float dir; public float thresh; }
        public static Spec Btn(int i) { return new Spec { kind = InKind.Button, idx = i, dir = 0, thresh = 0 }; }
        public static Spec Ax(int a, float dir) { return new Spec { kind = InKind.Axis, idx = a, dir = dir, thresh = 0.5f }; }
        public static bool Active(Spec s, Snapshot snap)
        {
            if (s.kind == InKind.Button) return snap.buttons != null && s.idx >= 0 && s.idx < snap.buttons.Length && snap.buttons[s.idx];
            if (snap.axes == null || s.idx < 0 || s.idx >= snap.axes.Length) return false;
            float v = snap.axes[s.idx];
            return s.dir > 0 ? v >= s.thresh : v <= -s.thresh;
        }
        static Snapshot s_prev;
        public static bool Down(Spec s, Snapshot cur) { return Active(s, cur) && !Active(s, s_prev); }

        public enum Act { NavUp, NavDown, NavLeft, NavRight, RUp, RDown, RLeft, RRight, Confirm, Cancel, Refine, Replace, Merge, Ready, Breakthrough, YuPing }
        static Dictionary<Act, Spec> s_bind;
        static void LoadDefaults()
        {
            s_bind = new Dictionary<Act, Spec> {
                { Act.NavLeft,  Ax(0, -1) }, { Act.NavRight, Ax(0, +1) },   // 左摇杆 LX 光标
                { Act.NavUp,    Ax(1, +1) }, { Act.NavDown,  Ax(1, -1) },    // LY 上=+
                { Act.RUp,    Ax(3, +1) }, { Act.RDown,  Ax(3, -1) },        // 右摇杆 RY 上=摆牌 下=收回(归一化 上=+)
                { Act.RLeft,  Ax(2, -1) }, { Act.RRight, Ax(2, +1) },        // 右摇杆 RX 左/右=换位
                { Act.Refine,       Btn(4) },   // LB 炼化
                { Act.Replace,      Btn(5) },   // RB 换牌
                { Act.Confirm,      Btn(1) },   // B(右) 确认/点击(保持现状)
                { Act.Cancel,       Btn(0) },   // A(下) 取消(保持现状)
                { Act.YuPing,       Btn(6) },   // Back/View/− 五行玉瓶(角色立绘是相机渲染的,导航够不到 → 专用键)
                // 合成=LT(轴4)单按 / 突破=□方块(Btn2)单按 / 准备=RT(轴5)长按 / 设置=Start(Btn7):走 Down/PollHold 专门处理(△三角=表情)
            };
        }
        // 长按检测:每按钮记按住帧数。短按=松开时未达阈值;长按=按住达阈值触发一次(本次按下只触发一次)
        const int LONG_HOLD = 24;   // ~0.4s @ 60fps
        static readonly Dictionary<int, int> s_holdN = new Dictionary<int, int>();
        static readonly HashSet<int> s_longFired = new HashSet<int>();
        static int HoldKey(Spec s) { return s.kind == InKind.Button ? s.idx : 100 + s.idx; }   // 按钮/轴分段做键,不冲突
        static void PollHold(Spec spec, Snapshot cur, out bool shortFire, out bool longFire)
        {
            shortFire = false; longFire = false;
            bool active = Active(spec, cur);
            int key = HoldKey(spec);
            int h; s_holdN.TryGetValue(key, out h);
            if (active) { h++; s_holdN[key] = h; if (h >= LONG_HOLD && !s_longFired.Contains(key)) { s_longFired.Add(key); longFire = true; } }
            else { if (h > 0 && h < LONG_HOLD && !s_longFired.Contains(key)) shortFire = true; if (h != 0) s_holdN[key] = 0; s_longFired.Remove(key); }
        }
        // 改键：LoadDefaults 之后把每个可改动作绑成配置项（默认值 = 当前 Spec 反格式化，避免与 LoadDefaults 重复）；
        // 用户在管理器改键，运行中每 ~0.5s 重新读入生效。绑定必须在 OnLoad 内完成才会进管理器配置表单。
        static readonly Act[] BIND_ACTS = { Act.NavUp, Act.NavDown, Act.NavLeft, Act.NavRight, Act.RUp, Act.RDown, Act.RLeft, Act.RRight, Act.Refine, Act.Replace, Act.Confirm, Act.Cancel, Act.YuPing };
        static readonly string[] BIND_KEYS = { "NavUp", "NavDown", "NavLeft", "NavRight", "RUp", "RDown", "RLeft", "RRight", "Refine", "Replace", "Confirm", "Cancel", "YuPing" };
        static readonly string[] BIND_DESC = { "上导航", "下导航", "左导航", "右导航", "右摇杆上=摆牌", "右摇杆下=收回", "右摇杆左=换位", "右摇杆右=换位", "炼化", "换牌", "确认", "取消", "五行玉瓶" };
        static readonly string[] BIND_DESC_EN = { "Nav Up", "Nav Down", "Nav Left", "Nav Right", "RStick Up = Place", "RStick Down = Recall", "RStick Left = Swap", "RStick Right = Swap", "Refine", "Reroll", "Confirm", "Cancel", "Jade Vase" };
        static ConfigEntry<string>[] s_bindCfg;
        static string FormatSpec(Spec s)
        {
            if (s.kind == InKind.Button) return "Button:" + s.idx.ToString(CultureInfo.InvariantCulture);
            return "Axis:" + s.idx.ToString(CultureInfo.InvariantCulture) + ":" + ((int)s.dir).ToString(CultureInfo.InvariantCulture);
        }
        static bool TryParseSpec(string v, out Spec spec)
        {
            spec = Btn(0);
            try {
                if (v == null) return false;
                var p = v.Split(':');
                if (p.Length >= 2 && p[0] == "Button") { spec = Btn(int.Parse(p[1], CultureInfo.InvariantCulture)); return true; }
                if (p.Length >= 3 && p[0] == "Axis") { spec = Ax(int.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture)); return true; }
            } catch (Exception) { }
            return false;
        }
        static void BindConfig(ModContext ctx)
        {
            if (ctx == null) return;
            s_bindCfg = new ConfigEntry<string>[BIND_ACTS.Length];
            for (int i = 0; i < BIND_ACTS.Length; i++) {
                string def = ""; Spec cur;
                if (s_bind != null && s_bind.TryGetValue(BIND_ACTS[i], out cur)) def = FormatSpec(cur);
                s_bindCfg[i] = ctx.Config.Bind("键位", BIND_KEYS[i], def, (s_en ? BIND_DESC_EN : BIND_DESC)[i] + T("（Button:序号 或 Axis:序号:方向）", " (Button:index or Axis:index:dir)"));
            }
        }
        static void ApplyOverrides()
        {
            if (s_bindCfg == null || s_bind == null) return;
            for (int i = 0; i < s_bindCfg.Length; i++) {
                var e = s_bindCfg[i]; if (e == null) continue;
                Spec sp; if (TryParseSpec(e.Value, out sp)) s_bind[BIND_ACTS[i]] = sp;
            }
        }

        // ── 手柄设置面板用：动作展示表 + 改键状态机 ──────────────
        static readonly string[] FIXED_NAMES = { "合成", "突破", "准备(长按RT)", "设置", "表情" };
        static readonly string[] FIXED_NAMES_EN = { "Fuse", "Breakthrough", "Ready (hold RT)", "Settings", "Emote" };
        static Spec FixedSpec(int fi)
        {
            if (fi == 0) return Ax(4, +1);   // 合成 = LT
            if (fi == 1) return Btn(2);      // 突破 = 方块/X
            if (fi == 2) return Ax(5, +1);   // 准备 = RT 长按
            if (fi == 3) return Btn(7);      // 设置 = Start
            return Btn(3);                   // 表情 = 三角/Y
        }
        /// <summary>面板显示的动作总数（可改 + 固定）。</summary>
        public static int ActionCount { get { return BIND_ACTS.Length + FIXED_NAMES.Length; } }
        /// <summary>第 i 个动作的中文名。</summary>
        public static string ActionName(int i) { return i < BIND_ACTS.Length ? (s_en ? BIND_DESC_EN : BIND_DESC)[i] : (s_en ? FIXED_NAMES_EN : FIXED_NAMES)[i - BIND_ACTS.Length]; }
        static Spec ActionSpec(int i)
        {
            if (i < BIND_ACTS.Length) { Spec s; if (s_bind != null && s_bind.TryGetValue(BIND_ACTS[i], out s)) return s; return Btn(-1); }
            return FixedSpec(i - BIND_ACTS.Length);
        }
        /// <summary>第 i 个动作当前绑定的可读文字。</summary>
        public static string ActionBindingText(int i) { return SpecText(ActionSpec(i)); }
        /// <summary>第 i 个动作此刻是否被触发（面板高亮用）。</summary>
        public static bool ActionActive(int i) { return Active(ActionSpec(i), Capture()); }
        /// <summary>第 i 个动作能不能改键（固定键返回 false）。</summary>
        public static bool ActionRebindable(int i) { return i < BIND_ACTS.Length; }

        static readonly string[] BTN_NAME = { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "L3", "R3", "Guide" };
        static readonly string[] AX_NAME = { "左摇杆X", "左摇杆Y", "右摇杆X", "右摇杆Y", "LT", "RT", "十字键X", "十字键Y" };
        static readonly string[] AX_NAME_EN = { "LStick X", "LStick Y", "RStick X", "RStick Y", "LT", "RT", "DPad X", "DPad Y" };
        /// <summary>按钮序号的可读名（面板实时输入用）。</summary>
        public static string ButtonLabel(int i) { return i >= 0 && i < BTN_NAME.Length ? BTN_NAME[i] : (T("按钮", "Button ") + i.ToString(CultureInfo.InvariantCulture)); }
        /// <summary>轴序号的可读名（面板实时输入用）。</summary>
        public static string AxisLabel(int i) { return i >= 0 && i < AX_NAME.Length ? (s_en ? AX_NAME_EN : AX_NAME)[i] : (T("轴", "Axis ") + i.ToString(CultureInfo.InvariantCulture)); }
        static string SpecText(Spec s)
        {
            if (s.kind == InKind.Button) {
                if (s.idx < 0) return "-";
                return (s.idx < BTN_NAME.Length ? BTN_NAME[s.idx] : (T("按钮", "Button ") + s.idx.ToString(CultureInfo.InvariantCulture))) + T(" 键", "");
            }
            string an = s.idx >= 0 && s.idx < AX_NAME.Length ? (s_en ? AX_NAME_EN : AX_NAME)[s.idx] : (T("轴", "Axis ") + s.idx.ToString(CultureInfo.InvariantCulture));
            return an + (s.dir > 0 ? " +" : " -");
        }

        // 改键：面板点「改键」→ BeginRebind → Tick 捕获下一个「新按下」→ 写配置 + 立即生效。
        static bool s_rebinding; static int s_rebindIdx = -1; static Snapshot s_rebindBase;
        /// <summary>正在改键吗。</summary>
        public static bool IsRebinding { get { return s_rebinding; } }
        /// <summary>正在改哪个动作（ActionName 下标）；没在改为 -1。</summary>
        public static int RebindingIndex { get { return s_rebindIdx; } }
        /// <summary>开始给第 i 个动作改键（只有可改的有效）。</summary>
        public static void BeginRebind(int i)
        {
            if (i < 0 || i >= BIND_ACTS.Length) return;
            s_rebindIdx = i; s_rebindBase = Capture(); s_rebinding = true;
        }
        /// <summary>取消改键。</summary>
        public static void CancelRebind() { s_rebinding = false; s_rebindIdx = -1; }

        // 手柄设置面板开着时：挂起游戏内导航（准星、卡牌、菜单），把手柄让给面板自己导航（GamepadPanel.HandleNav）。
        // 改键捕获不受影响（在这个开关之前处理）。
        static bool s_panelOpen;
        /// <summary>告诉导航层「手柄设置面板开/关」。开着时 Tick 不驱动游戏，只让改键捕获照常。</summary>
        public static void SetPanelOpen(bool open) { s_panelOpen = open; }
        static void HandleRebind(Snapshot cur)
        {
            for (int b = 0; b < NBTN; b++)
                if (Active(Btn(b), cur) && !Active(Btn(b), s_rebindBase)) { SaveRebind(Btn(b)); return; }
            for (int a = 0; a < NAX; a++) {
                if (Active(Ax(a, +1), cur) && !Active(Ax(a, +1), s_rebindBase)) { SaveRebind(Ax(a, +1)); return; }
                if (Active(Ax(a, -1), cur) && !Active(Ax(a, -1), s_rebindBase)) { SaveRebind(Ax(a, -1)); return; }
            }
        }
        static void SaveRebind(Spec sp)
        {
            int i = s_rebindIdx;
            if (i >= 0 && s_bindCfg != null && i < s_bindCfg.Length && s_bindCfg[i] != null && s_bind != null) {
                s_bind[BIND_ACTS[i]] = sp;                                            // 立即生效
                try { s_bindCfg[i].Value = FormatSpec(sp); } catch (Exception) { }   // 持久化（发 configChanged 由管理器写回配置文件）
            }
            s_rebinding = false; s_rebindIdx = -1;
        }

        public static bool DownAct(Act act, Snapshot cur) { Spec s; return s_bind != null && s_bind.TryGetValue(act, out s) && Down(s, cur); }
        public static bool ActiveAct(Act act, Snapshot cur) { Spec s; return s_bind != null && s_bind.TryGetValue(act, out s) && Active(s, cur); }   // 持续(按住)
        static readonly int[] s_navHold = new int[4];   // 各方向按住帧数(0=未按),用于长按自动连发
        // 自动连发:初次按下立即触发;持续按住 → 延迟 ~0.33s 后每 ~0.1s 重复一次
        static bool NavRepeat(int dir, bool active)
        {
            if (!active) { s_navHold[dir] = 0; return false; }
            int h = s_navHold[dir]++;
            if (h == 0) return true;
            return h >= 20 && (h - 20) % 6 == 0;
        }

        // ── 目标 + 空间导航 ───────────────────────────────────────
        class Tgt { public RectTransform rt; public Vector2 c; public object card; public int handIdx = -1; public int boardIdx = -1; public Selectable sel; public int order; }
        static readonly List<Tgt> s_tgts = new List<Tgt>();
        static RectTransform s_selRt; static Vector2 s_selC; static bool s_haveSelC;
        static object s_curCard; static int s_curHandIdx = -1, s_curBoardIdx = -1; static Selectable s_curSel;
        static List<MonoBehaviour> s_clickCache = new List<MonoBehaviour>(); static int s_uiCd;
        static bool s_battle;   // 当前在战斗内(cp!=null):轻量采集 + 普通导航,别碰菜单那套重逻辑
        static int s_wantHand = -1, s_wantBoard = -1, s_wantTtl = 0;   // 动作后重选:跟随换/摆/换位的那张牌
        static object s_wantCardObj;   // 换牌后按【对象引用】钉住焦点(换牌走服务器异步、原地 InitData 复用同一 CardItem,index 会变但对象不变)
        static Vector2 s_pinLastC, s_pinHomeC; static int s_pinStable;   // 钉住期间:上一帧位置 / home位(换牌前原位) / 连续稳定帧数(只认牌回 home 才落定,中途停顿不误判)
        static int s_dumpCd;                                          // 诊断 dump 节流
        static readonly HashSet<RectTransform> s_occluded = new HashSet<RectTransform>(); static int s_occCd;   // 射线遮挡剔除(UI 目标,节流3帧)
        static readonly List<RaycastResult> s_rr = new List<RaycastResult>();
        static MonoBehaviour s_fullClick;   // 最高层的全屏点击处理器(点任意处继续)
        static RectTransform s_lastSelLog;  // 上次记名的选中(诊断:选中变化即记名)
        static ScrollRect s_scrollSr;       // 滚动时锁定的 ScrollRect(防选中框飞出滚动区后断滚)
        static GameObject s_scrollGO;       // 滚动时锁定的 IScrollHandler GO(FancyScrollView 等非 ScrollRect 列表:弈闻/好友/成就…)
        static Transform s_modal;           // 当前隔离到的模态面板根(DoCancel 在它子树内找关闭按钮,因面板可能跨层 ReturnButton 在低层)

        // 面板解析 / 卡牌可操作 / 五行玉瓶格 —— 全部经绑定层 GamepadApi（不在导航层引用游戏类型）。

        static void RefreshUI()   // 每 ~15 帧扫一次场上可点元素
        {
            var list = new List<MonoBehaviour>();
            try {
                if (s_battle) {
                    // 战斗内:只扫 Button+Toggle(轻量)。卡牌走 CardPanel API,绝不全量扫 MonoBehaviour(会卡 + 把卡牌自带触发器当目标污染选中)
                    var bs = UnityEngine.Object.FindObjectsOfType<Button>();
                    if (bs != null) foreach (var b in bs) { if (b == null || !b.isActiveAndEnabled || !b.IsInteractable()) continue; var n = b.gameObject.name; if (n != null && n.StartsWith("GP_")) continue; if (IsDecoration(b.transform)) continue; list.Add(b); }
                    var ts = UnityEngine.Object.FindObjectsOfType<Toggle>();
                    if (ts != null) foreach (var t in ts) { if (t == null || !t.isActiveAndEnabled || !t.IsInteractable()) continue; var n = t.gameObject.name; if (n != null && n.StartsWith("GP_")) continue; if (IsDecoration(t.transform)) continue; list.Add(t); }
                    // 长按触发器(头像/玩家信息项 → 点击切换看对手牌局):特定类型、实例很少,安全(不像 UniRx 满场都是)
                    var lp = GamepadApi.LongPressTriggers();
                    for (int _li = 0; _li < lp.Count; _li++) { var l = lp[_li]; if (l == null || !l.isActiveAndEnabled) continue; var n = l.gameObject.name; if (n != null && n.StartsWith("GP_")) continue; if (IsUnderCard(l.transform)) continue; if (IsDecoration(l.transform)) continue; list.Add(l); }
                    // 战斗内开着的弹窗(表情/道韵/天衍 选择面板):格子是 UniRx 触发器,上面那套扫不到。只扫弹窗面板【子树】内的 IPointerClickHandler(靠 CanvasGroup 定位,数量少,不全场扫,避免卡顿)
                    var cgs = UnityEngine.Object.FindObjectsOfType<CanvasGroup>();
                    if (cgs != null) foreach (var cg in cgs) {
                        if (cg == null || !cg.isActiveAndEnabled || cg.alpha < 0.5f) continue;
                        var cn = cg.gameObject.name; if (cn == null) continue;
                        // BoxSubPanelBase(表情/关键词/切段菜单等)的 CanvasGroup 就挂在名为 "Box" 的子物体上(boxRT=Find("Box"));其余弹窗按关键词
                        if (cn != "Box" && cn.IndexOf("Emoji") < 0 && cn.IndexOf("Selection") < 0 && cn.IndexOf("Popup") < 0 && cn.IndexOf("Strategy") < 0) continue;
                        var comps = cg.GetComponentsInChildren(typeof(MonoBehaviour), false);
                        if (comps != null) foreach (var c in comps) {
                            var m = c as MonoBehaviour; if (m == null || !m.isActiveAndEnabled || !(m is IPointerClickHandler)) continue;
                            var sel2 = m as Selectable; if (sel2 != null && !sel2.IsInteractable()) continue;
                            var nm = m.gameObject.name; if (nm != null && nm.StartsWith("GP_")) continue;
                            if (IsUnderCard(m.transform) || IsDecoration(m.transform)) continue;
                            list.Add(m);
                        }
                    }
                } else {
                    // 菜单内:扫所有 IPointerClickHandler(Button/Toggle/EventTrigger/UniRx ObservablePointerClickTrigger)
                    var ms = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                    if (ms != null) foreach (var m in ms) {
                        if (m == null || !m.isActiveAndEnabled) continue;
                        bool ok = m is IPointerClickHandler;
                        if (!ok && (m is IPointerDownHandler) && UnderNameContains(m.transform, "DivinationPanel")) ok = true;   // 卜筮"翻乌龟"(爻 DivinationYaoItem 的骨骼图)只有 OnPointerDown 触发器(IPointerDownHandler),Click 扫不到 → 单独补进来
                        if (!ok) continue;
                        var sel = m as Selectable; if (sel != null && !sel.IsInteractable()) continue;
                        var n = m.gameObject.name; if (n != null && n.StartsWith("GP_")) continue;
                        if (IsDecoration(m.transform)) continue;                                         // 背景立绘/看板装饰(龙球等)不当目标
                        list.Add(m);
                    }
                }
            } catch (Exception) { }
            s_clickCache = list;
        }

        static void GatherTargets(bool hasCp)
        {
            s_tgts.Clear();
            float sw = 0f, sh = 0f; try { sw = Screen.width; sh = Screen.height; } catch (Exception) { }
            // 手牌 + 棋盘 — 仅摆牌阶段(卡牌可操作)才采集;斗法阶段游戏禁掉了卡牌交互,这时绝不能再把手牌/棋盘当导航目标
            if (hasCp && GamepadApi.CardsOperable()) {
                var hcs = GamepadApi.HandCards();
                for (int i = 0; i < hcs.Count; i++) { var t = hcs[i]; AddT(t.Rt, t.Card, t.HandIdx, -1, null, sw, sh); }
                var yg = GamepadApi.YuPingTargets();
                if (yg != null) {
                    // 玉瓶开着:牌桌在面板背后(选它会"穿透") → 改采集瓶内 3 格(复用 boardIdx,玉瓶开着时 boardIdx 一律指瓶格)。
                    for (int i = 0; i < yg.Count; i++) { var t = yg[i]; AddT(t.Rt, t.Card, -1, t.BoardIdx, null, sw, sh); }
                } else {
                    var bg = GamepadApi.BoardGrids();
                    for (int i = 0; i < bg.Count; i++) { var t = bg[i]; AddT(t.Rt, t.Card, -1, t.BoardIdx, null, sw, sh); }
                }
            }
            // 所有可点击组件(IPointerClickHandler:Button/Toggle/EventTrigger/UniRx 触发器);近全屏(>70%屏)的不进导航(留给"点任意处继续"/DoCancel)
            s_fullClick = null;
            if (s_clickCache != null) for (int i = 0; i < s_clickCache.Count; i++) {
                var m = s_clickCache[i]; if (m == null || !m.isActiveAndEnabled) continue;
                var rt = m.transform as RectTransform; if (rt == null) continue;
                Vector2 ec, ez; float ea; if (!ScreenRect(rt, out ec, out ez, out ea)) continue;
                if (sw > 1 && ez.x > sw * 0.7f && ez.y > sh * 0.7f) {   // 全屏 → 不进导航,记为"点任意处继续"候选(取层最高)
                    if (s_fullClick == null || LayerRank(rt) >= LayerRank(s_fullClick.transform as RectTransform)) s_fullClick = m;
                    continue;
                }
                AddT(rt, null, -1, -1, m as Selectable, sw, sh);
            }
            for (int i = 0; i < s_tgts.Count; i++) s_tgts[i].order = LayerRank(s_tgts[i].rt);   // 真实 UI 层(已修)
            int maxL = -1; for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].order > maxL) maxL = s_tgts[i].order;
            if (maxL >= 2) {
                // 真正高层模态(Popup/Top/Block):无论菜单/战斗,只留最高层 → 设置/聊天/好友详情/排行榜详情/放弃确认/结算/问号说明等,背后全剔。
                Transform mr = null;
                for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].order == maxL) { mr = PanelRootOnLayer(s_tgts[i].rt); break; }
                for (int i = s_tgts.Count - 1; i >= 0; i--) if (s_tgts[i].order != maxL) s_tgts.RemoveAt(i);
                if (mr != null && s_battle) GatherModalSubtree(mr, sw, sh);   // 战斗内高层模态:补扫 UniRx/FancyScrollView 内容
                s_modal = mr;
            } else {
                // 摆牌阶段"查看对手":OpponentInfoPanel 激活 → 剔除自己手牌/牌桌(防向下穿透选到自己的牌),并补扫对手面板子树(对手仙命/用牌都带 UniRx 点击触发器 → 可导航,悬浮上去显示对手仙命)
                Transform oppRoot = s_battle ? FindOpponentPanel() : null;
                if (oppRoot != null) {
                    for (int i = s_tgts.Count - 1; i >= 0; i--) if (s_tgts[i].handIdx >= 0 || s_tgts[i].boardIdx >= 0) s_tgts.RemoveAt(i);   // 自己的手牌(handIdx≥0)/牌桌格(boardIdx≥0)全剔
                    GatherModalSubtree(oppRoot, sw, sh);   // 对手仙命图标/用牌(BattleTalentIconItem/CardItem 的 OnPointerClick 触发器)→ 可悬浮看仙命;玩家头像/左上按钮保留可切换
                    s_modal = oppRoot;
                } else {
                // Normal/Fixed:菜单做图层+遮挡(战斗不做,保卡牌手感);再用【命名】FindModalPanel 处理同层模态(图鉴/秘籍/秘籍详情)
                if (!s_battle) {
                    for (int i = s_tgts.Count - 1; i >= 0; i--) if (s_tgts[i].order != maxL) s_tgts.RemoveAt(i);
                    if (s_occCd++ % 6 == 0) RecomputeOcclusion();
                    for (int i = s_tgts.Count - 1; i >= 0; i--) if (s_occluded.Contains(s_tgts[i].rt)) s_tgts.RemoveAt(i);
                } else if (s_occluded.Count > 0) s_occluded.Clear();
                var modal = FindModalPanel();
                s_modal = modal;
                if (modal != null) {
                    if (s_battle) GatherModalSubtree(modal, sw, sh);
                    for (int i = s_tgts.Count - 1; i >= 0; i--) if (!IsUnder(s_tgts[i].rt, modal)) s_tgts.RemoveAt(i);
                } else if (s_battle && s_fullClick != null) {
                    var settle = ContinuePanelRoot(s_fullClick);   // 战斗结算/结束"点任意处继续"屏:全屏 Bg 盖住卡牌/按钮(同层不隔离会穿透)→ 清空背后目标,只留 ○/× 继续(ClickFullScreen 会点 ConfirmButton)
                    if (settle != null) { s_tgts.Clear(); s_modal = settle; }
                }
                }
            }
        }
        static void RecomputeOcclusion()
        {
            s_occluded.Clear();
            try {
                var es = EventSystem.current; if (es == null) return;
                var ped = PED(); if (ped == null) return;
                for (int i = 0; i < s_tgts.Count; i++) {
                    var t = s_tgts[i];
                    if (t.rt == s_selRt) continue;   // 当前选中的不剔除:它的悬浮提示/弹窗会盖住它,别因此剔了导致选中丢失→反复闪烁(仙命缩略图等)
                    if (t.card != null || t.handIdx >= 0 || t.boardIdx >= 0) continue;   // 卡牌/格子不查(空格子常无 raycast 图形)
                    ped.position = t.c; s_rr.Clear(); es.RaycastAll(ped, s_rr);
                    if (s_rr.Count == 0) continue;                                        // 该点无命中 → 不算遮挡
                    var hit = s_rr[0].gameObject; if (hit == null) continue;
                    Transform ht = hit.transform, tt = t.rt; bool related = false;
                    for (Transform p = ht; p != null; p = p.parent) if (p == tt) { related = true; break; }   // 目标是命中的祖先
                    if (!related) for (Transform p = tt; p != null; p = p.parent) if (p == ht) { related = true; break; }   // 命中是目标的祖先
                    if (!related) s_occluded.Add(t.rt);                                   // 命中的是别的东西 → 被盖住
                }
            } catch (Exception) { }
        }
        static bool IsUnder(Transform t, Transform anc) { try { for (Transform p = t; p != null; p = p.parent) if (p == anc) return true; } catch (Exception) { } return false; }
        static bool UnderNameContains(Transform t, string key) { try { for (Transform p = t; p != null; p = p.parent) { var n = p.gameObject.name; if (n != null && n.IndexOf(key) >= 0) return true; } } catch (Exception) { } return false; }
        // 摆牌阶段"查看对手"时激活的 OpponentInfoPanel(它子树里有已采集目标=BackButton/对手用牌/仙命 → 说明正在看对手)
        static Transform FindOpponentPanel()
        {
            try {
                for (int i = 0; i < s_tgts.Count; i++)
                    for (Transform p = s_tgts[i].rt; p != null; p = p.parent)
                        if (p.gameObject.name == "OpponentInfoPanel") return p;
            } catch (Exception) { }
            return null;
        }
        // 模态面板名(精确到具体面板,避免误伤 KeywordDetailPanel 等悬浮提示):道韵/仙命/天衍(*SelectionPanel/*StrategyPanel/*PopupPanel)+ 设置/好友(聊天)/秘籍详情/图鉴/玩家菜单
        static readonly string[] MODAL_NAMES = { "SelectionPanel", "StrategyPanel", "PopupPanel", "SettingsPanel", "FriendPanel", "EsotericDetailPanel", "EsotericMainPanel", "CardIllustrationPanel", "PlayerMenuPanel", "DivinationPanel" };
        // 找当前打开的弹窗面板(靠已采集目标的祖先链命中模态名),用于隔离(只留弹窗内目标)
        static Transform FindModalPanel()
        {
            try {
                for (int i = 0; i < s_tgts.Count; i++)
                    for (Transform p = s_tgts[i].rt; p != null; p = p.parent) {
                        var n = p.gameObject.name; if (n == null) continue;
                        for (int k = 0; k < MODAL_NAMES.Length; k++) if (n.IndexOf(MODAL_NAMES[k]) >= 0) return p;
                    }
            } catch (Exception) { }
            return null;
        }
        // 战斗内补扫模态面板【子树】的 IPointerClickHandler(轻扫只收 Button/Toggle,漏了秘籍详情等的 FancyScrollView/UniRx 内容)
        static readonly List<MonoBehaviour> s_modalCache = new List<MonoBehaviour>(); static Transform s_modalCacheFor; static int s_modalCd;
        static void GatherModalSubtree(Transform modal, float sw, float sh)
        {
            try {
                if (s_modalCacheFor != modal || s_modalCd++ % 12 == 0) {   // 换面板 或 每 12 帧:重扫子树(GetComponentsInChildren 贵,大量卡牌时尤甚)→ 缓存 IPointerClickHandler 列表
                    s_modalCache.Clear(); s_modalCacheFor = modal;
                    var comps = modal.GetComponentsInChildren(typeof(MonoBehaviour), false);
                    if (comps != null) foreach (var c in comps) { var m = c as MonoBehaviour; if (m != null && m is IPointerClickHandler) s_modalCache.Add(m); }
                }
                int before = s_tgts.Count;
                for (int i = 0; i < s_modalCache.Count; i++) {
                    var m = s_modalCache[i]; if (m == null || !m.isActiveAndEnabled) continue;
                    var sel = m as Selectable; if (sel != null && !sel.IsInteractable()) continue;
                    var rt = m.transform as RectTransform; if (rt == null) continue;
                    var n = m.gameObject.name; if (n != null && n.StartsWith("GP_")) continue;
                    if (IsDecoration(m.transform)) continue;
                    AddT(rt, null, -1, -1, sel, sw, sh);
                }
                for (int i = before; i < s_tgts.Count; i++) s_tgts[i].order = LayerRank(s_tgts[i].rt);
            } catch (Exception) { }
        }
        // 真实 UI 层 = RootPanel 的【直接子】(NormalLayer/PopupLayer/TopLayer/BlockLayer/FixedLayer)。
        // 关键:面板内部常有同名子容器("TopLayer"/"Fixed"),不能认它们,否则层级被骗(图鉴/秘籍误判成高层)。
        static int LayerRank(RectTransform rt)
        {
            try {
                for (Transform t = rt; t != null; t = t.parent) {
                    if (t.parent == null || t.parent.gameObject.name != "RootPanel") continue;   // 只认 RootPanel 的直接子
                    var n = t.gameObject.name;
                    if (n == "BlockLayer") return 4;
                    if (n == "TopLayer") return 3;
                    if (n == "PopupLayer") return 2;
                    if (n == "NormalLayer") return 1;
                    if (n == "FixedLayer") return 0;
                    return 1;
                }
            } catch (Exception) { }
            return 1;   // 没找到层节点 → 按 Normal
        }
        // 真实高层模态的面板根 = 层节点(RootPanel 直接子)下面那一层(如 PopupLayer/FriendPanel、TopLayer/MessageBox)
        static Transform PanelRootOnLayer(RectTransform rt)
        {
            try {
                Transform prev = rt;
                for (Transform t = rt; t != null; t = t.parent) {
                    if (t.parent != null && t.parent.gameObject.name == "RootPanel") return prev;   // t=层节点, prev=它的子=面板根
                    prev = t;
                }
            } catch (Exception) { }
            return null;
        }
        static int SortOrder(RectTransform rt)
        {
            try {
                var c = rt.GetComponentInParent(typeof(Canvas)) as Canvas; if (c == null) return 0;
                if (c.overrideSorting) return c.sortingOrder;
                var root = c.rootCanvas; return root != null ? root.sortingOrder : c.sortingOrder;
            } catch (Exception) { return 0; }
        }
        // 卡牌区域(父链名字判断,不碰热更 CardItem 类型,避免原生 GetComponentInParent 崩溃)
        static bool IsUnderCard(Transform t)
        {
            try { for (Transform p = t; p != null; p = p.parent) { var n = p.gameObject.name; if (n != null && (n.IndexOf("CardItem") >= 0 || n.IndexOf("CardGrid") >= 0 || n.IndexOf("HandCardLayout") >= 0 || n.IndexOf("CardGridLayout") >= 0)) return true; } } catch (Exception) { }
            return false;
        }
        // 背景立绘/看板装饰(龙球 DragonBallFollower 等):不当导航目标
        static bool IsDecoration(Transform t)
        {
            try {
                for (Transform p = t; p != null; p = p.parent) {
                    var n = p.gameObject.name; if (n == null) continue;
                    if (n.IndexOf("Follower") >= 0 || n.IndexOf("DragonBall") >= 0) return true;                       // 龙球等随从装饰
                    if (n.IndexOf("Kanban") >= 0 || n.IndexOf("CharacterRoot") >= 0 || n.IndexOf("BackgroundPanel") >= 0) return true;   // 立绘/看板背景
                    if (n.IndexOf("DaoYinListItem") >= 0) return true;   // 道引显示项(当前道引"有缘杯",非操作目标,别选)
                    if ((n.IndexOf("DescriptionPanel") >= 0 && n.IndexOf("ModeDescriptionPanel") < 0) || n.IndexOf("KeywordDetailPanel") >= 0) return true;   // BoxSubPanelBase 自跟随悬浮提示(仙命/关键词/卡牌/道韵...纯展示,常在高层→别当目标,否则光标被它抢走反复闪烁);ModeDescriptionPanel 例外:它是 ILRSubPanelBase 真弹窗,有 ReturnButton 需可选可关
                }
            } catch (Exception) { }
            return false;
        }
        // 在所属裁剪视口(RectMask2D)内吗:滚出视口被 Mask 裁掉的菜单项不当目标(防选中框飞出滚动区)
        static bool InViewport(RectTransform rt, Vector2 center)
        {
            try {
                var mask = rt.GetComponentInParent(typeof(RectMask2D)) as RectMask2D;
                if (mask == null) return true;
                var vp = mask.transform as RectTransform;
                Vector2 vc, vs; float va; if (!ScreenRect(vp, out vc, out vs, out va)) return true;
                return Math.Abs(center.x - vc.x) <= vs.x * 0.5f + 6f && Math.Abs(center.y - vc.y) <= vs.y * 0.5f + 6f;
            } catch (Exception) { return true; }
        }
        // 仿 Unity:沿父链找 CanvasGroup,任一 interactable=false → 不可交互(模态弹窗会把背后界面置灰)
        static bool RectInteractable(RectTransform rt)
        {
            try {
                Transform t = rt;
                while (t != null) {
                    var cg = t.GetComponent(typeof(CanvasGroup)) as CanvasGroup;
                    if (cg != null) { if (!cg.interactable) return false; if (cg.ignoreParentGroups) break; }
                    t = t.parent;
                }
            } catch (Exception) { }
            return true;
        }
        // 诊断:把当前目标列表(名字/层级/尺寸/类型)写日志,便于排查"选到没用的东西/选到背后界面"
        static void DumpTargets(string tag)
        {
            try {
                var sb = new System.Text.StringBuilder(); sb.Append("DUMP ").Append(tag).Append(" n=").Append(s_tgts.Count).Append("\r\n");
                for (int i = 0; i < s_tgts.Count; i++) {
                    var t = s_tgts[i]; string nm = "?"; try { nm = t.rt.gameObject.name; } catch (Exception) { }
                    string pp = ""; try { var p = t.rt.parent; for (int d = 0; d < 12 && p != null; d++) { pp = p.gameObject.name + "/" + pp; p = p.parent; } } catch (Exception) { }
                    string kind = t.handIdx >= 0 ? ("hand" + t.handIdx) : t.boardIdx >= 0 ? ("board" + t.boardIdx) : t.sel is Toggle ? "toggle" : t.sel is Button ? "button" : "ui";
                    sb.Append("  [").Append(kind).Append("] '").Append(nm).Append("' ord=").Append(t.order).Append(" c=").Append((int)t.c.x).Append(",").Append((int)t.c.y).Append(" parent=").Append(pp).Append("\r\n");
                }
                W(sb.ToString());
            } catch (Exception) { }
        }
        static string FullPath(Transform t) { try { string pp = t.gameObject.name; for (Transform p = t.parent; p != null; p = p.parent) pp = p.gameObject.name + "/" + pp; return pp; } catch (Exception) { return "?"; } }
        // 诊断:屏幕中心射线命中链 + 组件(查"点击任意位置继续"屏的全屏处理器到底是什么类型)
        static void DumpCenter()
        {
            try {
                var es = EventSystem.current; var ped = PED(); if (es == null || ped == null) return;
                Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f); ped.position = c;
                s_rr.Clear(); es.RaycastAll(ped, s_rr);
                var sb = new System.Text.StringBuilder(); sb.Append("CENTER hits=").Append(s_rr.Count).Append("\r\n");
                for (int i = 0; i < s_rr.Count && i < 6; i++) {
                    var go = s_rr[i].gameObject; if (go == null) continue;
                    string comps = ""; try { var cs = go.GetComponents(typeof(Component)); foreach (var cc in cs) if (cc != null) comps += cc.GetType().Name + " "; } catch (Exception) { }
                    string pp = ""; try { Transform p = go.transform.parent; for (int d = 0; d < 4 && p != null; d++) { pp = p.gameObject.name + "/" + pp; p = p.parent; } } catch (Exception) { }
                    bool pc = false, pd = false; try { pc = go.GetComponent(typeof(IPointerClickHandler)) != null; pd = go.GetComponent(typeof(IPointerDownHandler)) != null; } catch (Exception) { }
                    sb.Append("  '").Append(go.name).Append("' click=").Append(pc).Append(" down=").Append(pd).Append(" parent=").Append(pp).Append(" comps=[").Append(comps).Append("]\r\n");
                }
                W(sb.ToString());
            } catch (Exception) { }
        }
        // 诊断:最高层里【未被采集】的可点图形(天梯/绘卷/仙命/图鉴卡等没有 Selectable/EventTrigger 的自定义可点)
        static void DumpClickables()
        {
            try {
                int top = -1; for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].order > top) top = s_tgts[i].order;
                var sb = new System.Text.StringBuilder(); sb.Append("CLICKABLES top=").Append(top).Append("\r\n");
                var gs = UnityEngine.Object.FindObjectsOfType<MaskableGraphic>(); int cnt = 0;
                if (gs != null) for (int i = 0; i < gs.Length && cnt < 50; i++) {
                    var g = gs[i]; if (g == null || !g.isActiveAndEnabled || !g.raycastTarget) continue;
                    var rt = g.transform as RectTransform; if (rt == null) continue;
                    if (LayerRank(rt) != top) continue;
                    if (g.GetComponentInParent(typeof(IPointerClickHandler)) != null) continue;   // 已能采集的(任何点击处理器)跳过
                    var n = g.gameObject.name; if (n != null && n.StartsWith("GP_")) continue;
                    string pp = ""; try { var p = rt.parent; for (int d = 0; d < 3 && p != null; d++) { pp = p.gameObject.name + "/" + pp; p = p.parent; } } catch (Exception) { }
                    string comps = ""; try { var cs = g.gameObject.GetComponents(typeof(Component)); foreach (var c in cs) if (c != null) comps += c.GetType().Name + " "; } catch (Exception) { }
                    sb.Append("  '").Append(n).Append("' parent=").Append(pp).Append(" comps=[").Append(comps).Append("]\r\n"); cnt++;
                }
                sb.Append("  共 ").Append(cnt).Append(" 个未采集 raycast 图形\r\n"); W(sb.ToString());
            } catch (Exception) { }
        }
        static void AddT(RectTransform rt, object card, int handIdx, int boardIdx, Selectable sel, float sw, float sh)
        {
            if (rt == null) return;
            for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].rt == rt) return;   // 去重(同物体上 Selectable+EventTrigger)
            var n = rt.gameObject.name; if (n != null && n.StartsWith("GP_")) return;   // 排除自己的准星层
            if (!RectInteractable(rt)) return;   // 模态遮挡:父级 CanvasGroup 不可交互 → 剔除(防选背后界面)
            Vector2 c, sz; float a;
            if (!ScreenRect(rt, out c, out sz, out a)) return;
            if (sw > 1 && (c.x < -40 || c.x > sw + 40 || c.y < -40 || c.y > sh + 40)) return;   // 屏外剔除
            // UI 目标(非卡牌,仅菜单内):滚出裁剪视口的剔除(防选中框飞出滚动区);战斗内不做
            if (!s_battle && card == null && handIdx < 0 && boardIdx < 0 && !InViewport(rt, c)) return;
            s_tgts.Add(new Tgt { rt = rt, c = c, card = card, handIdx = handIdx, boardIdx = boardIdx, sel = sel, order = SortOrder(rt) });
        }

        static ScrollRect s_selScroll;   // 当前选中所在的 ScrollRect(回退时优先留在同列表内)
        static Tgt FindCurrent()
        {
            for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].rt == s_selRt) return s_tgts[i];   // 维持上次选中
            Tgt best = null; float bd = float.MaxValue;
            // 选中丢了(常因滚动把它裁出视口):优先选【同一 ScrollRect】内离上次位置最近的,别跳出列表
            if (s_selScroll != null) {
                for (int i = 0; i < s_tgts.Count; i++) { if (ScrollOf(s_tgts[i].rt) != s_selScroll) continue; float d = s_haveSelC ? Mag(s_tgts[i].c - s_selC) : 0f; if (d < bd) { bd = d; best = s_tgts[i]; } }
                if (best != null) return best;
            }
            bd = float.MaxValue;   // 同列表内没有了 → 全局最近,否则第一个
            for (int i = 0; i < s_tgts.Count; i++) { float d = s_haveSelC ? Mag(s_tgts[i].c - s_selC) : 0f; if (d < bd) { bd = d; best = s_tgts[i]; } }
            return best;
        }
        static ScrollRect ScrollOf(RectTransform rt) { try { return rt == null ? null : rt.GetComponentInParent(typeof(ScrollRect)) as ScrollRect; } catch (Exception) { return null; } }
        static Tgt NavBest(Tgt cur, Vector2 dir, ScrollRect restrict)   // 先紧锥(~58°),找不到再宽锥兜底(够到偏轴/孤立目标)
        {
            var b = NavBestCone(cur, dir, restrict, 1.6f);
            return b != null ? b : NavBestCone(cur, dir, restrict, 4.0f);
        }
        static Tgt NavBestCone(Tgt cur, Vector2 dir, ScrollRect restrict, float cone)
        {
            if (cur == null) return null;
            Tgt best = null; float bestScore = float.MaxValue;
            for (int i = 0; i < s_tgts.Count; i++) {
                var t = s_tgts[i]; if (t == cur) continue;
                if (restrict != null && ScrollOf(t.rt) != restrict) continue;
                Vector2 d = t.c - cur.c;
                float along = d.x * dir.x + d.y * dir.y;       // 沿 dir 投影
                if (along < 10f) continue;                      // 必须在该方向上
                float perp = Mag(new Vector2(d.x - along * dir.x, d.y - along * dir.y));
                if (perp > along * cone) continue;              // 锥内
                float score = along + perp * 2.2f;
                if (score < bestScore) { bestScore = score; best = t; }
            }
            return best;
        }
        static void Select(Tgt t) { if (t != null) { s_selRt = t.rt; s_selC = t.c; s_haveSelC = true; } }
        // FancyScrollView 容器:选中项祖先里实现 IScrollHandler 的 GO(中间无标准 ScrollRect)。弈闻/好友/成就/秘术等列表都是它
        static GameObject FancyScrollerOf(RectTransform rt)
        {
            try { for (Transform p = rt; p != null; p = p.parent) { if (p.GetComponent(typeof(ScrollRect)) != null) return null; if (p.GetComponent(typeof(IScrollHandler)) != null) return p.gameObject; } } catch (Exception) { }
            return null;
        }
        static int WheelDir(Vector2 dir) { return dir.y < -0.5f ? -1 : dir.y > 0.5f ? 1 : dir.x > 0.5f ? -1 : 1; }   // 导航方向→滚轮:下/右=露出后面(-1),上/左=露出前面(+1)
        static Tgt NavBestFancy(Tgt cur, Vector2 dir, GameObject fancy)   // 同一 Fancy 容器内、dir 方向上最近的可见格
        {
            if (cur == null) return null;
            Tgt best = null; float bestScore = float.MaxValue;
            for (int i = 0; i < s_tgts.Count; i++) {
                var t = s_tgts[i]; if (t == cur) continue;
                if (FancyScrollerOf(t.rt) != fancy) continue;
                Vector2 d = t.c - cur.c;
                float along = d.x * dir.x + d.y * dir.y; if (along < 10f) continue;
                float perp = Mag(new Vector2(d.x - along * dir.x, d.y - along * dir.y)); if (perp > along * 4.0f) continue;
                float score = along + perp * 2.2f; if (score < bestScore) { bestScore = score; best = t; }
            }
            return best;
        }
        static int s_fancyStuck;   // Fancy 列表沿滚动轴连续滚动没出新格的次数(到头检测,够大就放行离开)
        // dir 是否沿该 Fancy 列表的滚动轴(纵向列表只认上下,横向只认左右)。纯几何:看可见格的包围盒哪个方向更长(偏向纵向,因为列表多为纵向且单行很宽易误判)。
        // 注:绝不用反射读原生 Scroller.ScrollDirection —— ILRuntime 对原生类型反射会抛逃出 try/catch 的内部错误,直接终结 Tick 订阅,手柄永久冻死。
        static bool FancyAxisMatches(GameObject fancy, Vector2 dir)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue; int n = 0;
            for (int i = 0; i < s_tgts.Count; i++) { var t = s_tgts[i]; if (FancyScrollerOf(t.rt) != fancy) continue; if (t.c.x < minX) minX = t.c.x; if (t.c.x > maxX) maxX = t.c.x; if (t.c.y < minY) minY = t.c.y; if (t.c.y > maxY) maxY = t.c.y; n++; }
            bool horizontal = n >= 2 && (maxX - minX) > (maxY - minY) * 1.5f;
            return horizontal ? Math.Abs(dir.x) > 0.5f : Math.Abs(dir.y) > 0.5f;
        }
        static bool CanScroll(ScrollRect sr, Vector2 dir)
        {
            try {
                var vp = sr.viewport != null ? sr.viewport : sr.transform as RectTransform;
                if (sr.content == null || vp == null) return false;
                float vOver = sr.content.rect.height - vp.rect.height;   // 纵向溢出量
                float hOver = sr.content.rect.width - vp.rect.width;     // 横向溢出量
                // 必须该轴 ScrollRect 标志开启【且】内容溢出 >8px 才算可滚。关键:纵向列表(sr.horizontal=false)的 content 可能比视口宽,
                // 但绝不该被当成可横滚(否则门派侧栏右推被困住、出不去到卡牌)。
                bool vScroll = sr.vertical && vOver > 8f, hScroll = sr.horizontal && hOver > 8f;
                // 主轴判定:哪个方向溢出更多就是主滚动轴,只认主轴方向(防纵向列表的内容略宽于视口→左右被误判成可横滚而把光标困住,出不去到旁边的卡牌)
                if (vScroll && hScroll) { if (vOver >= hOver) hScroll = false; else vScroll = false; }
                if (vScroll && (dir.y > 0.5f || dir.y < -0.5f)) { float v = sr.verticalNormalizedPosition; return dir.y < 0 ? v > 0.01f : v < 0.99f; }
                if (hScroll && (dir.x > 0.5f || dir.x < -0.5f)) { float h = sr.horizontalNormalizedPosition; return dir.x > 0 ? h < 0.99f : h > 0.01f; }
            } catch (Exception) { }
            return false;
        }
        static void ScrollChunk(ScrollRect sr, Vector2 dir)   // 往 dir 滚一截(露出下一项)
        {
            try {
                const float step = 0.10f;
                if (sr.vertical && (dir.y > 0.5f || dir.y < -0.5f)) { sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + (dir.y < 0 ? -step : step)); sr.velocity = Vector2.zero; }
                else if (sr.horizontal && (dir.x > 0.5f || dir.x < -0.5f)) { sr.horizontalNormalizedPosition = Mathf.Clamp01(sr.horizontalNormalizedPosition + (dir.x > 0 ? step : -step)); sr.velocity = Vector2.zero; }
            } catch (Exception) { }
        }
        static int s_navDbg;
        static string SN(Transform t) { try { return t == null ? "null" : t.gameObject.name; } catch (Exception) { return "?"; } }
        // 返回 true=选中变了(需 EnsureVisible 跟随);滚动一截返回 false(本身就是滚动,不再跟随以免回弹)
        static Tgt HandByIndex(int idx) { if (idx < 0) return null; for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].handIdx == idx) return s_tgts[i]; return null; }
        static bool NavMove(Tgt cur0, Vector2 dir)
        {
            // 卡牌(手牌/棋盘)走普通导航:棋盘在 CardScroll 里,绝不限定同 ScrollRect / 误滚棋盘
            bool isCard = cur0.card != null || cur0.handIdx >= 0 || cur0.boardIdx >= 0;
            // 手牌左右:按 index 走相邻手牌(手牌扇形重叠时,空间导航会漏掉某些牌)
            if (cur0.handIdx >= 0 && (dir.x > 0.5f || dir.x < -0.5f)) {
                var ht = HandByIndex(cur0.handIdx + (dir.x > 0 ? 1 : -1));
                if (ht != null) { Select(ht); return true; }   // 到边了(没有相邻手牌)→ 落回下面的空间导航(去棋盘/UI)
            }
            var sr = isCard ? null : ScrollOf(cur0.rt);
            if (sr != null) {
                var best = NavBest(cur0, dir, sr);                       // 先在同一 ScrollRect 内找下一项
                if (best != null) { Select(best); return true; }
                if (CanScroll(sr, dir)) { ScrollChunk(sr, dir); return false; }   // 同列表内没下一项但能滚 → 滚一截露出,不跳出
                if (s_navDbg++ % 3 == 0) W("navmove 跳出: cur=" + SN(cur0.rt) + " sr=" + SN(sr.transform) + " dir=" + (int)dir.x + "," + (int)dir.y + " vNP=" + sr.verticalNormalizedPosition.ToString("F2") + " vert=" + sr.vertical);
            } else if (!isCard) {
                // FancyScrollView 列表(弈闻/好友等,无标准 ScrollRect):只沿滚动轴滚,垂直方向(纵向列表的左右)放行去全局导航离开列表
                var fancy = FancyScrollerOf(cur0.rt);
                if (fancy != null) {
                    var best = NavBestFancy(cur0, dir, fancy);
                    if (best != null) {   // 同容器内有下一可见格 → 选它;若它已是该方向最后可见格,滚一格预露出下一格(选中框跟着列表滚)
                        s_fancyStuck = 0; Select(best);
                        if (NavBestFancy(best, dir, fancy) == null && FancyAxisMatches(fancy, dir)) DoScrollWheel(fancy, WheelDir(dir));
                        return true;
                    }
                    if (FancyAxisMatches(fancy, dir)) {   // 沿滚动轴、已是最后可见格 → 滚露出;连续多次都没新格(到头)才放行离开
                        DoScrollWheel(fancy, WheelDir(dir));
                        if (++s_fancyStuck < 6) return false;
                    }
                    s_fancyStuck = 0;   // 垂直于滚动轴 或 滚到头 → 落到下面的全局导航,允许离开列表
                }
                if (s_navDbg++ % 3 == 0) W("navmove 无SR: cur=" + SN(cur0.rt));
            }
            var b2 = NavBest(cur0, dir, null);
            if (b2 != null) { Select(b2); if (s_navDbg % 3 == 0) W("  → 跳到 " + SN(b2.rt)); return true; }
            return false;
        }

        // 卡牌悬浮放大(游戏自带 OnPointerEnter/Exit)经绑定层 GamepadApi.HoverCard 处理(内部反射托管 CardItem,安全)。
        static PointerEventData s_ped;
        static PointerEventData PED() { if (s_ped == null) { try { s_ped = new PointerEventData(EventSystem.current); } catch (Exception) { } } return s_ped; }
        static void SetHover(object card) { GamepadApi.HoverCard(card); }
        // 非卡牌目标:派发 pointerEnter/Exit(ExecuteHierarchy 会沿父链找处理器),触发游戏的悬浮提示(仙命缩略图/关键词图标/天衍等的 tooltip)
        static GameObject s_hoverGo;
        static void SetHoverGeneral(GameObject go)
        {
            if (s_hoverGo == go) return;   // 仅选中变化时派发一次
            var ped = PED();
            try { if (ped != null && s_hoverGo != null) ExecuteEvents.ExecuteHierarchy(s_hoverGo, ped, ExecuteEvents.pointerExitHandler); } catch (Exception) { }
            GamepadApi.HideTooltips();     // 离开旧元素 → 关掉之前被撑着的提示
            s_hoverGo = go;
            try { if (ped != null && go != null) { ped.position = s_selC; ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler); } } catch (Exception) { }
            GamepadApi.SuppressTooltips(); // 进入新元素后,若弹出提示则禁掉它的自动隐藏 → 不动鼠标也不消失
        }
        // 悬浮提示自动隐藏抑制 / 无关闭键悬浮卡的关闭 —— 移到绑定层 GamepadApi.SuppressTooltips / HideTooltips / HideFloatingPanels。

        // ── 动作 ──────────────────────────────────────────────────
        static void Reselect(int hand, int board) { s_wantHand = hand; s_wantBoard = board; s_wantCardObj = null; s_wantTtl = 14; }   // 动作后下几帧把选中落到这张牌(按 index)
        static void ReselectCard(object c) { s_wantCardObj = c; s_wantHand = -1; s_wantBoard = -1; s_wantTtl = 240; s_pinStable = 0; s_pinLastC = s_selC; s_pinHomeC = s_selC; }   // 换牌后:~4s 内把焦点钉在这张牌(对象不变),准星冻在 home,牌飞回 home 才落定
        // 牌位动作(摆/收/换/炼/换牌/合成/突破/玉瓶存取)全部经绑定层 GamepadApi，返回要重选的 boardIdx / 卡牌句柄由导航层落位。
        // 突破是否可用:玩家自身信息项上的 "LevelUpBtn"(升级/突破按钮)激活且可点时才有突破可发(攒够修为才出现)。点它就是 PendingTalentReq
        static bool CanBreakthrough()
        {
            try { var btns = UnityEngine.Object.FindObjectsOfType<Button>(); if (btns != null) foreach (var b in btns) { if (b == null) continue; if (b.gameObject.name == "LevelUpBtn" && b.isActiveAndEnabled && b.IsInteractable()) return true; } } catch (Exception) { }
            return false;
        }
        static ScrollRect FindScrollRect()   // 当前目标所在的 ScrollRect,否则取最高层一个可滚动的
        {
            ScrollRect sr = null;
            try {
                if (s_selRt != null) sr = s_selRt.GetComponentInParent(typeof(ScrollRect)) as ScrollRect;
                if (sr == null) {
                    int top = -1; for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].order > top) top = s_tgts[i].order;
                    var srs = UnityEngine.Object.FindObjectsOfType<ScrollRect>();
                    if (srs != null) foreach (var s in srs) { if (s == null || !s.isActiveAndEnabled) continue; if (LayerRank(s.transform as RectTransform) != top) continue; sr = s; break; }
                }
            } catch (Exception) { }
            return sr;
        }
        static void EnsureVisible(RectTransform target)   // 导航后:滚动所在 ScrollRect 让选中项进入视口(选中框始终留在滚动范围内)
        {
            try {
                if (target == null) return;
                var sr = target.GetComponentInParent(typeof(ScrollRect)) as ScrollRect;
                if (sr == null || sr.content == null) return;
                var vp = sr.viewport != null ? sr.viewport : sr.transform as RectTransform;
                if (vp == null) return;
                var wc = new Vector3[4]; target.GetWorldCorners(wc);
                float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
                for (int i = 0; i < 4; i++) { var lp = vp.InverseTransformPoint(wc[i]); if (lp.y < minY) minY = lp.y; if (lp.y > maxY) maxY = lp.y; if (lp.x < minX) minX = lp.x; if (lp.x > maxX) maxX = lp.x; }
                var vr = vp.rect; float margin = 10f;
                if (sr.vertical) {
                    float over = 0f;
                    if (maxY > vr.yMax - margin) over = maxY - (vr.yMax - margin);          // 超出上边
                    else if (minY < vr.yMin + margin) over = minY - (vr.yMin + margin);     // 超出下边
                    float scrollable = sr.content.rect.height - vr.height;
                    if (scrollable > 1f && Math.Abs(over) > 0.5f) { sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + over / scrollable); sr.velocity = Vector2.zero; }
                }
                if (sr.horizontal) {
                    float over = 0f;
                    if (maxX > vr.xMax - margin) over = maxX - (vr.xMax - margin);
                    else if (minX < vr.xMin + margin) over = minX - (vr.xMin + margin);
                    float scrollable = sr.content.rect.width - vr.width;
                    if (scrollable > 1f && Math.Abs(over) > 0.5f) { sr.horizontalNormalizedPosition = Mathf.Clamp01(sr.horizontalNormalizedPosition + over / scrollable); sr.velocity = Vector2.zero; }
                }
            } catch (Exception) { }
        }
        // 翻页(ScrollSnapPagination)移到绑定层 GamepadApi.PageFlip。当前最高层级由导航层算好传入。
        static int TopRank() { int top = -1; for (int i = 0; i < s_tgts.Count; i++) if (s_tgts[i].order > top) top = s_tgts[i].order; return top; }
        static int s_scrollDbg;
        static void DoScroll(ScrollRect sr, float dir)   // dir>0 上滚;符号待校正
        {
            try {
                if (s_scrollDbg++ % 20 == 0) W("scroll dir=" + dir + " sr=" + (sr == null ? "null" : sr.gameObject.name));
                if (sr == null) return;
                if (sr.vertical) { sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + dir * 0.03f); sr.velocity = Vector2.zero; }
                else if (sr.horizontal) { sr.horizontalNormalizedPosition = Mathf.Clamp01(sr.horizontalNormalizedPosition - dir * 0.03f); sr.velocity = Vector2.zero; }
            } catch (Exception ex) { W("scroll err " + ex.Message); }
        }
        // 决定右摇杆滚动谁:① 选中项祖先里的标准 ScrollRect;② 否则祖先里实现 IScrollHandler 的(FancyScrollView Scroller:弈闻/好友/成就/秘术…);③ 都没有 → 退而取最高层一个 ScrollRect
        static void ResolveScroll()
        {
            s_scrollSr = null; s_scrollGO = null;
            try {
                for (Transform p = s_selRt; p != null; p = p.parent) {
                    var sr = p.GetComponent(typeof(ScrollRect)) as ScrollRect;
                    if (sr != null) { s_scrollSr = sr; return; }
                    if (p.GetComponent(typeof(IScrollHandler)) != null) { s_scrollGO = p.gameObject; return; }
                }
            } catch (Exception) { }
            s_scrollSr = FindScrollRect();
        }
        // 给 IScrollHandler 发滚轮事件(不引用具体 FancyScrollView.Scroller 类型,避免热更未绑定该原生类型而崩);dir>0 上滚
        static void DoScrollWheel(GameObject go, float dir)
        {
            try {
                if (go == null) return;
                var ped = PED(); if (ped == null) return;
                ped.position = s_selC; ped.scrollDelta = new Vector2(0f, dir * 1.0f);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.scrollHandler);
            } catch (Exception) { }
        }
        // 准备 / 五行玉瓶 / 设置 / 表情 全部经绑定层 GamepadApi.Ready / OpenYuPing / OpenSettings / ToggleEmoji。
        // 准备位若是"抉择"(roguelike RogueOptionButton 激活):点开抉择面板,返回 true;否则 false(交给正常准备)
        static bool TryClickRogueOption()
        {
            try { var btns = UnityEngine.Object.FindObjectsOfType<Button>(); if (btns != null) foreach (var b in btns) { if (b == null) continue; if (b.gameObject.name == "RogueOptionButton" && b.isActiveAndEnabled && b.IsInteractable()) { if (b.onClick != null) b.onClick.Invoke(); return true; } } } catch (Exception) { }
            return false;
        }
        static void DoConfirm()   // ○ = 点击/选中当前 UI 目标
        {
            try {
                // 没有任何可选目标(s_selRt 为空)→ 多半是"点击任意位置继续"屏(结算/失败/抽卡):○ 也触发全屏点击(× 同样能,见 DoCancel)
                if (s_selRt == null && s_curSel == null && s_curCard == null && s_curHandIdx < 0 && s_curBoardIdx < 0) { if (ClickFullScreen()) return; }
                if (s_curSel != null) {
                    var tg = s_curSel as Toggle; if (tg != null) { tg.isOn = true; return; }
                    var bt = s_curSel as Button; if (bt != null && bt.onClick != null) { bt.onClick.Invoke(); return; }
                }
                // 非 Button/Toggle(EventTrigger / UniRx 触发器等);仅 UI,不点卡牌
                if (s_curSel == null && s_curCard == null && s_curHandIdx < 0 && s_curBoardIdx < 0 && s_selRt != null) {
                    var go = s_selRt.gameObject;
                    if (go.GetComponent(typeof(IDragHandler)) != null || go.GetComponent(typeof(IBeginDragHandler)) != null) { DoDragToCenter(go); return; }   // 绘卷包:拖到中央打开
                    var ped = PED();
                    if (ped != null) {
                        try { ped.position = s_selC; ped.pressPosition = s_selC; ped.button = PointerEventData.InputButton.Left; } catch (Exception) { }
                        ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerDownHandler);    // down+up:LongPressedEventTrigger 的短按(头像→看对手牌局)
                        ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerUpHandler);
                        ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerClickHandler);   // 普通点击(Observable 触发器等)
                    }
                }
            } catch (Exception ex) { W("confirm err " + ex.Message); }
        }
        static void DoDragToCenter(GameObject go)   // 模拟把物体拖到屏幕中央(绘卷包打开;经过中央 OpenArea 时触发其 enter)
        {
            try {
                var es = EventSystem.current; var ped = new PointerEventData(es); ped.button = PointerEventData.InputButton.Left;
                Vector2 start = s_selC, end = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                ped.position = start; ped.pressPosition = start;
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.beginDragHandler);
                Vector2 prev = start; GameObject lastEnter = null;
                for (int i = 1; i <= 10; i++) {
                    Vector2 p = Vector2.Lerp(start, end, i / 10f);
                    ped.delta = p - prev; ped.position = p; prev = p;
                    ExecuteEvents.Execute(go, ped, ExecuteEvents.dragHandler);
                    // 经过点下方的物体发 pointerEnter(中央 OpenArea 靠 enter 触发打开)
                    try { s_rr.Clear(); if (es != null) es.RaycastAll(ped, s_rr); var top = s_rr.Count > 0 ? s_rr[0].gameObject : null;
                        if (top != lastEnter) { if (lastEnter != null) ExecuteEvents.Execute(lastEnter, ped, ExecuteEvents.pointerExitHandler); if (top != null) ExecuteEvents.Execute(top, ped, ExecuteEvents.pointerEnterHandler); lastEnter = top; } } catch (Exception) { }
                }
                ExecuteEvents.Execute(go, ped, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerUpHandler);
                W("drag-to-center " + go.name);
            } catch (Exception ex) { W("drag err " + ex.Message); }
        }
        // 强名(明确的返回/关闭)优先于弱名 "back"(避免 CardBackButton=卡背 被当成返回)
        static readonly string[] CLOSE_STRONG = { "return", "close", "关闭", "返回", "退出", "quit", "exit", "cancel" };   // cancel:确认框的"取消/继续"=×关闭
        static readonly string[] CLOSE_WEAK = { "back" };
        static void ClickTgt(Tgt t)   // 点一个目标:Button→onClick,Toggle→翻转,其它(EventTrigger/UniRx)→ExecuteEvents
        {
            try {
                var b = t.sel as Button; if (b != null && b.onClick != null) { b.onClick.Invoke(); return; }
                var tg = t.sel as Toggle; if (tg != null) { tg.isOn = !tg.isOn; return; }
                var ped = PED(); if (ped != null && t.rt != null) { ped.position = t.c; ped.pressPosition = t.c; ExecuteEvents.Execute(t.rt.gameObject, ped, ExecuteEvents.pointerDownHandler); ExecuteEvents.Execute(t.rt.gameObject, ped, ExecuteEvents.pointerUpHandler); ExecuteEvents.Execute(t.rt.gameObject, ped, ExecuteEvents.pointerClickHandler); }
            } catch (Exception) { }
        }
        // 战斗结算/结束"点任意处继续"面板:它们的全屏 Bg 往往是空 Button(没监听),真正继续是面板里的 ConfirmButton→OnConfrimButtonClick(逐窗 ShowNextWindow→最后 LeaveBattle)
        static readonly string[] CONTINUE_PANELS = { "SettlePanel", "SettleCupFinalPanel", "YiXianLuSettlePanel", "SkipBattleResultPanel" };
        static Transform ContinuePanelRoot(MonoBehaviour full)
        {
            try { if (full == null) return null; for (Transform p = full.transform; p != null; p = p.parent) { var n = p.gameObject.name; if (n == null) continue; for (int k = 0; k < CONTINUE_PANELS.Length; k++) if (n.IndexOf(CONTINUE_PANELS[k]) >= 0) return p; } } catch (Exception) { } return null;
        }
        static bool ClickSettleConfirm(Transform settleRoot)   // 在结算面板子树里找 ConfirmButton/ContinueButton(再退求 Close/Return)点它继续
        {
            try {
                if (settleRoot == null) return false;
                var btns = settleRoot.GetComponentsInChildren(typeof(Button), false); if (btns == null) return false;
                for (int pass = 0; pass < 2; pass++) foreach (var bo in btns) {
                    var b = bo as Button; if (b == null || !b.isActiveAndEnabled || !b.IsInteractable() || b.onClick == null) continue;
                    var n = b.gameObject.name; if (n == null) continue;
                    bool match = pass == 0 ? (n.IndexOf("Confirm") >= 0 || n.IndexOf("Continue") >= 0) : (n.IndexOf("Close") >= 0 || n.IndexOf("Return") >= 0);
                    if (match) { b.onClick.Invoke(); W("settle confirm " + n); return true; }
                }
            } catch (Exception) { }
            return false;
        }
        // "点击任意位置继续"(结算/失败/抽卡等全屏点一下的屏):s_fullClick=近全屏(>70%屏)点击处理器。在它中心派发完整 Enter+Down+Up+Click 序列(有的处理器认 down,有的认 click)
        static bool ClickFullScreen()
        {
            try {
                if (s_fullClick == null || !s_fullClick.isActiveAndEnabled) return false;
                var sp = ContinuePanelRoot(s_fullClick); if (sp != null && ClickSettleConfirm(sp)) return true;   // 结算面板:点真正的 ConfirmButton(Bg 多半是空 Button)
                var fb = s_fullClick as Button; if (fb != null && fb.onClick != null) { fb.onClick.Invoke(); W("fullscreen btn " + SN(s_fullClick.transform)); return true; }
                var go = s_fullClick.gameObject; var ped = PED(); if (ped == null) return false;
                Vector2 c = s_selC; try { c = new Vector2(Screen.width / 2f, Screen.height / 2f); } catch (Exception) { }
                ped.position = c; ped.pressPosition = c; try { ped.button = PointerEventData.InputButton.Left; } catch (Exception) { }
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerClickHandler);
                W("fullscreen exec " + SN(s_fullClick.transform)); return true;
            } catch (Exception) { return false; }
        }
        static void DoCancel()   // × = 返回/关闭。在已采集(已隔离到当前模态)的目标里找关闭键(不限 Button:CloseButton 常是 UniRx 可点);找不到再点全屏挡板
        {
            W("DOCANCEL called sel=" + SN(s_selRt) + " modal=" + SN(s_modal) + " ntgts=" + s_tgts.Count + " full=" + (s_fullClick != null ? SN(s_fullClick.transform) : "null"));
            try {
                if (GamepadApi.HideFloatingPanels()) { W("cancel floating(FriendDetail)"); return; }   // 排行榜/好友玩家详情:无关闭键,直接 Hide
                Tgt strong = null, weak = null;
                for (int i = 0; i < s_tgts.Count; i++) {
                    var t = s_tgts[i]; if (t.rt == null) continue;
                    var n = t.rt.gameObject.name; if (n == null || n.StartsWith("GP_")) continue;
                    var nl = n.ToLowerInvariant();
                    bool ms = false; for (int k = 0; k < CLOSE_STRONG.Length; k++) if (nl.IndexOf(CLOSE_STRONG[k]) >= 0) { ms = true; break; }
                    if (ms) { if (strong == null) strong = t; continue; }
                    bool mw = false; for (int k = 0; k < CLOSE_WEAK.Length; k++) if (nl.IndexOf(CLOSE_WEAK[k]) >= 0) { mw = true; break; }
                    if (mw) { if (weak == null) weak = t; }
                }
                var target = strong != null ? strong : weak;   // close/return/cancel/返回... > back
                if (target != null) { ClickTgt(target); W("cancel " + target.rt.gameObject.name); return; }
                // 没具名关闭键 → 点任意处关闭/继续:近全屏点击处理器(s_fullClick:BlurBackground/Bg/Mask/继续遮罩 等)
                if (ClickFullScreen()) { W("cancel→fullscreen"); return; }
            } catch (Exception ex) { W("cancel err " + ex.Message); }
        }
        // 合成(找同名可升级配对 + 执行)移到绑定层 GamepadApi.Merge(选中卡句柄)。

        // ── 准星(四角圆角描边,滑动+呼吸+跟随倾角)──────────────────
        static GameObject s_canvas, s_ret; static RectTransform s_retRt;
        static Image[] s_corners; static Sprite s_brk;
        static Vector2 s_dC, s_dS; static bool s_haveD; static float s_dA, s_phase;
        const float BR_SIZE = 26f;
        static readonly Color RET_COL = new Color(1f, 0.85f, 0.25f, 1f);
        static readonly Vector2[] CANCH = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        static readonly Vector2[] CFLIP = { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) };
        static float Mag(Vector2 v) { return (float)Math.Sqrt(v.x * v.x + v.y * v.y); }
        static Sprite MakeBracketSprite()
        {
            try {
                int N = 40, T = 8, O = 2, R = 13;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false); tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
                var clear = new Color(0, 0, 0, 0); var gold = new Color(1f, 0.85f, 0.25f, 1f); var dark = new Color(0.08f, 0.05f, 0f, 0.95f);
                for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) {
                    bool inArm = (x < T) || (y < T);
                    float cd = (x < R && y < R) ? (float)Math.Sqrt((R - x) * (R - x) + (R - y) * (R - y)) : -1f;
                    if (cd > R) inArm = false;
                    if (!inArm) { tex.SetPixel(x, y, clear); continue; }
                    bool edge = (x < O) || (y < O) || (x >= T - O && x < T && y >= T) || (y >= T - O && y < T && x >= T) || (x > N - 1 - O && y < T) || (y > N - 1 - O && x < T);
                    if (cd >= 0 && cd > R - O) edge = true;
                    tex.SetPixel(x, y, edge ? dark : gold);
                }
                tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0f, 0f));
            } catch (Exception) { return null; }
        }
        static void BuildReticle()
        {
            s_canvas = new GameObject("GP_Canvas");
            var cv = s_canvas.AddComponent(typeof(Canvas)) as Canvas; cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 30000;
            s_ret = new GameObject("GP_Reticle"); s_retRt = s_ret.AddComponent(typeof(RectTransform)) as RectTransform;
            s_retRt.SetParent(s_canvas.transform, false);
            s_retRt.anchorMin = new Vector2(0, 0); s_retRt.anchorMax = new Vector2(0, 0); s_retRt.pivot = new Vector2(0.5f, 0.5f);
            s_brk = MakeBracketSprite(); s_corners = new Image[4];
            for (int i = 0; i < 4; i++) {
                var go = new GameObject("c" + i); var rt = go.AddComponent(typeof(RectTransform)) as RectTransform; rt.SetParent(s_retRt, false);
                rt.anchorMin = CANCH[i]; rt.anchorMax = CANCH[i]; rt.pivot = new Vector2(0, 0); rt.anchoredPosition = new Vector2(0, 0); rt.sizeDelta = new Vector2(BR_SIZE, BR_SIZE);
                rt.localScale = new Vector3(CFLIP[i].x, CFLIP[i].y, 1f);
                var img = go.AddComponent(typeof(Image)) as Image; img.raycastTarget = false; if (s_brk != null) img.sprite = s_brk; else img.color = RET_COL;
                s_corners[i] = img;
            }
        }
        static bool ScreenRect(RectTransform rt, out Vector2 center, out Vector2 size, out float angle)
        {
            center = Vector2.zero; size = Vector2.zero; angle = 0f;
            try {
                var corners = new Vector3[4]; rt.GetWorldCorners(corners);
                var canvas = rt.GetComponentInParent(typeof(Canvas)) as Canvas; if (canvas != null) canvas = canvas.rootCanvas;
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
                Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 tl = RectTransformUtility.WorldToScreenPoint(cam, corners[1]);
                Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                Vector2 br = RectTransformUtility.WorldToScreenPoint(cam, corners[3]);
                center = new Vector2((bl.x + tr.x) * 0.5f, (bl.y + tr.y) * 0.5f);
                size = new Vector2(Mag(br - bl), Mag(tl - bl));
                angle = (float)(Math.Atan2(br.y - bl.y, br.x - bl.x) * 180.0 / Math.PI);
                return size.x > 1f && size.y > 1f;
            } catch (Exception) { return false; }
        }
        static void HideReticle() { if (s_ret != null) s_ret.SetActive(false); s_haveD = false; }
        static void ShowReticle(RectTransform target)
        {
            if (target == null) { HideReticle(); return; }
            Vector2 tc, ts; float ta;
            if (!ScreenRect(target, out tc, out ts, out ta)) { HideReticle(); return; }
            if (s_canvas == null) BuildReticle();
            s_ret.SetActive(true);
            if (!s_haveD) { s_dC = tc; s_dS = ts; s_dA = ta; s_haveD = true; }
            else { s_dC.x += (tc.x - s_dC.x) * 0.25f; s_dC.y += (tc.y - s_dC.y) * 0.25f; s_dS.x += (ts.x - s_dS.x) * 0.25f; s_dS.y += (ts.y - s_dS.y) * 0.25f; s_dA += (ta - s_dA) * 0.25f; }
            s_phase += 0.016f; float pulse = 1f + 0.04f * (float)Math.Sin(s_phase * 4.0);
            s_retRt.anchoredPosition = s_dC; s_retRt.localEulerAngles = new Vector3(0f, 0f, s_dA);
            float cw = s_dS.x, ch = s_dS.y;
            if (s_curCard == null && s_curHandIdx < 0 && s_curBoardIdx < 0) { if (cw > 300f) cw = 300f; if (ch > 210f) ch = 210f; }   // 仅大 UI 按钮(天梯)封顶;卡牌完整贴合
            s_retRt.sizeDelta = new Vector2(cw * pulse + 14f, ch * pulse + 14f);
        }

        // ── 入口 + 主循环 ─────────────────────────────────────────
        public static void Init(ModContext ctx)
        {
            s_en = ctx != null && ctx.Lang == "en";
            LoadDefaults(); BindConfig(ctx); ApplyOverrides();
        }
        public static void Tick()
        {
            bool hasCp = GamepadApi.HasCardPanel();
            s_battle = hasCp;                              // 战斗内:轻量采集 + 卡牌走 API,别用全量扫描(会卡 + 污染卡牌选中)
            if (s_uiCd++ % 15 == 0) RefreshUI();
            if (s_uiCd % 30 == 0) ApplyOverrides();        // 管理器改键 ~0.5s 内生效
            GatherTargets(hasCp);
            if (DIAG && s_dumpCd++ % 45 == 0) { W("==== battle=" + s_battle + " sel=" + SN(s_selRt) + " modal=" + SN(s_modal) + " fullPath=" + (s_fullClick != null ? FullPath(s_fullClick.transform) + " L" + LayerRank(s_fullClick.transform as RectTransform) : "null")); DumpTargets("now"); if (s_tgts.Count <= 2 || (s_battle && s_fullClick != null)) DumpCenter(); }   // 每 ~0.75s dump(诊断)
            var cur = Capture();
            // 改键中：只捕获要绑的键，不驱动游戏（按下的键不会同时触发原动作）。
            if (s_rebinding) { HandleRebind(cur); s_prev = cur; return; }
            // 手柄设置面板开着（且不在改键）：挂起游戏导航，手柄交给面板自己导航（GamepadMod 里调 panel.HandleNav）。
            if (s_panelOpen) { HideReticle(); SetHover(null); SetHoverGeneral(null); s_prev = cur; return; }
            if (DIAG) for (int _bi = 0; _bi < NBTN; _bi++) { if (Active(Btn(_bi), cur) && !Active(Btn(_bi), s_prev)) W("== BtnDown " + _bi); }   // 任何按钮按下都记(诊断)
            // 长按按钮(每帧都要 poll 以正确计时):L2(Btn6)短按合成/长按突破、R2(Btn7)长按准备。在 empty-return 前算,确保不论有无目标都正确计时
            bool r2Short, r2Long;
            PollHold(Ax(5, +1), cur, out r2Short, out r2Long);   // RT 准备长按(仅此保留长按)
            if (Down(Btn(7), cur)) GamepadApi.OpenSettings();           // Start → 设置(任意界面)
            if (hasCp && Down(Btn(3), cur)) GamepadApi.ToggleEmoji();    // △ → 切换表情面板
            if (hasCp && DownAct(Act.YuPing, cur)) GamepadApi.OpenYuPing();   // Back → 五行玉瓶(在 empty-return 前,面板盖住摆牌时也能开/再开)
            if (hasCp && r2Long) { if (!TryClickRogueOption()) GamepadApi.Ready(); }   // R2 长按:准备位是抉择 → 开抉择;否则准备
            if (s_tgts.Count == 0) {                       // 没目标:仍允许"点任意处继续"(○)和返回(×)
                HideReticle(); SetHover(null); SetHoverGeneral(null);
                if (s_fullClick != null && DownAct(Act.Confirm, cur)) { try { var ped = PED(); if (ped != null) { ExecuteEvents.Execute(s_fullClick.gameObject, ped, ExecuteEvents.pointerClickHandler); W("fullclick " + s_fullClick.gameObject.name); } } catch (Exception) { } }
                if (DownAct(Act.Cancel, cur)) DoCancel();
                s_prev = cur; return;
            }

            // 换牌后:按对象引用把焦点"钉"在那张牌上。飞行/翻面/网络往返期间【冻结】准星(停在原位不跟着乱跳),
            // 牌稳定落回手牌再显示在它上面;玩家一导航就放手。换牌走服务器异步 + 原地 InitData 复用同对象,故认对象不认 index。
            if (s_wantTtl > 0 && s_wantCardObj != null) {
                bool navInput = ActiveAct(Act.NavUp, cur) || ActiveAct(Act.NavDown, cur) || ActiveAct(Act.NavLeft, cur) || ActiveAct(Act.NavRight, cur)
                             || Active(Ax(6, +1), cur) || Active(Ax(6, -1), cur) || Active(Ax(7, +1), cur) || Active(Ax(7, -1), cur);
                if (navInput) { s_wantCardObj = null; s_wantTtl = 0; }   // 玩家自己动 → 放手,落到下面正常导航
                else {
                    Tgt found = null;
                    for (int i = 0; i < s_tgts.Count; i++) { var t = s_tgts[i]; if (t.card != null && t.card == s_wantCardObj) { found = t; break; } }
                    s_wantTtl--;
                    bool atHome = found != null && Mag(found.c - s_pinHomeC) < 50f;   // 牌在它的手牌 home 位(换牌前原位);飞到换牌区/半路时不在 home
                    if (found != null) { s_selRt = found.rt; s_selC = found.c; s_haveSelC = true; }   // 始终跟住对象(落点正确)
                    if (atHome && Mag(found.c - s_pinLastC) < 6f) s_pinStable++; else s_pinStable = 0;
                    if (found != null) s_pinLastC = found.c;
                    bool settled = atHome && s_pinStable >= 4 && (240 - s_wantTtl) >= 10;   // 回 home + 稳定 + 过了起始几帧(避免飞之前就误判落定)
                    if (settled || s_wantTtl <= 0) { s_wantCardObj = null; s_wantTtl = 0; }   // 落定/超时 → 放手,落到正常流程显示在这张牌
                    else if (atHome) { s_curCard = found.card; s_curHandIdx = found.handIdx; s_curBoardIdx = found.boardIdx; s_curSel = found.sel; SetHover(found.card); SetHoverGeneral(null); ShowReticle(found.rt); s_prev = cur; return; }   // 在 home(飞前/翻面/飞回)→ 显示在它上,短路
                    else { s_prev = cur; return; }   // 飞行中/没找到 → 冻结准星(停在 home 原位,不动)
                }
            }
            // 摆/换位后重选(按 index,本地即时)
            if (s_wantTtl > 0 && s_wantCardObj == null && (s_wantHand >= 0 || s_wantBoard >= 0)) {
                bool hit = false, handPresent = false;
                for (int i = 0; i < s_tgts.Count; i++) { var t = s_tgts[i]; if (t.handIdx >= 0) handPresent = true; if ((s_wantHand >= 0 && t.handIdx == s_wantHand) || (s_wantBoard >= 0 && t.boardIdx == s_wantBoard)) { s_selRt = t.rt; s_selC = t.c; s_haveSelC = true; hit = true; break; } }
                if (hit) { s_wantTtl = 0; s_wantHand = -1; s_wantBoard = -1; }
                else if (s_wantHand >= 0 && !handPresent) { /* 手牌全飞走还没回来 → 不倒计时,继续等 */ }
                else { s_wantTtl--; if (s_wantTtl <= 0) { s_wantHand = -1; s_wantBoard = -1; } }
            }

            var cur0 = FindCurrent();
            bool navMoved = false;
            if (cur0 != null) {                            // 左摇杆 → 空间导航
                bool navTap = false;   // 左摇杆 或 方向键(a[4]/a[5]);长按自动连发
                bool aU = ActiveAct(Act.NavUp, cur)    || Active(Ax(7, +1), cur);   // DpadY 上
                bool aD = ActiveAct(Act.NavDown, cur)  || Active(Ax(7, -1), cur);   // DpadY 下
                bool aL = ActiveAct(Act.NavLeft, cur)  || Active(Ax(6, -1), cur);   // DpadX 左
                bool aR = ActiveAct(Act.NavRight, cur) || Active(Ax(6, +1), cur);   // DpadX 右
                if (NavRepeat(0, aU)) { if (NavMove(cur0, new Vector2(0, 1)))  navMoved = true; if (s_navHold[0] == 1) navTap = true; }
                if (NavRepeat(1, aD)) { if (NavMove(cur0, new Vector2(0, -1))) navMoved = true; if (s_navHold[1] == 1) navTap = true; }
                if (NavRepeat(2, aL)) { if (NavMove(cur0, new Vector2(-1, 0))) navMoved = true; if (s_navHold[2] == 1) navTap = true; }
                if (NavRepeat(3, aR)) { if (NavMove(cur0, new Vector2(1, 0)))  navMoved = true; }
            }
            if (navMoved) { s_wantTtl = 0; s_wantHand = -1; s_wantBoard = -1; s_wantCardObj = null; }   // 玩家自己导航了 → 放手,别再钉住重选
            var sel = FindCurrent();                       // 导航后的当前目标
            if (sel == null) { HideReticle(); SetHover(null); SetHoverGeneral(null); s_prev = cur; return; }
            s_selRt = sel.rt; s_selC = sel.c; s_haveSelC = true;
            s_curCard = sel.card; s_curHandIdx = sel.handIdx; s_curBoardIdx = sel.boardIdx; s_curSel = sel.sel;
            bool selCard = sel.card != null || sel.handIdx >= 0 || sel.boardIdx >= 0;
            s_selScroll = selCard ? null : ScrollOf(sel.rt);   // 卡牌不记 ScrollRect(棋盘在 CardScroll 里,别触发回退留列表/自动滚)
            if (navMoved && !selCard) EnsureVisible(sel.rt);   // 仅菜单项滚动跟随;卡牌不自动滚(别滚棋盘)

            SetHover(sel.card);
            SetHoverGeneral(selCard ? null : (sel.rt != null ? sel.rt.gameObject : null));   // 非卡牌目标 → 派发 hover 触发提示(仙命缩略图/关键词等);卡牌走上面的反射 hover
            ShowReticle(sel.rt);

            if (hasCp) {
                if (DownAct(Act.RUp, cur)) { int ei = GamepadApi.Place(s_curHandIdx); if (ei >= 0) Reselect(-1, ei); }
                if (DownAct(Act.RDown, cur)) GamepadApi.Evict(s_curBoardIdx);
                if (DownAct(Act.RLeft, cur)) { int j = GamepadApi.Swap(s_curBoardIdx, -1); if (j >= 0) Reselect(-1, j); }
                if (DownAct(Act.RRight, cur)) { int j = GamepadApi.Swap(s_curBoardIdx, +1); if (j >= 0) Reselect(-1, j); }
                if (DownAct(Act.Refine, cur)) GamepadApi.Refine(s_curHandIdx);    // L1 炼化
                if (DownAct(Act.Replace, cur)) { object c = GamepadApi.Replace(s_curHandIdx); if (c != null) ReselectCard(c); }  // R1 换牌
                if (Down(Ax(4, +1), cur)) GamepadApi.Merge(s_curCard);         // L2 单按 = 合成(升级选中卡牌)
                if (Down(Btn(2), cur) && CanBreakthrough()) GamepadApi.Breakthrough();   // □方块 单按 = 突破(不可用则不发)
            }
            // 右摇杆在 UI 目标上 → 滚动(卡牌上才是摆牌/换位);锁定滚动器,按住期间持续滚它(选中框飞出滚动区也不断)
            bool rUp = ActiveAct(Act.RUp, cur), rDown = ActiveAct(Act.RDown, cur);
            if ((rUp || rDown) && s_curCard == null && s_curHandIdx < 0 && s_curBoardIdx < 0) {
                if (s_scrollSr == null && s_scrollGO == null) ResolveScroll();
                if (s_scrollSr != null) { if (rUp) DoScroll(s_scrollSr, +1f); if (rDown) DoScroll(s_scrollSr, -1f); }
                else if (s_scrollGO != null) { if (rUp) DoScrollWheel(s_scrollGO, +1f); if (rDown) DoScrollWheel(s_scrollGO, -1f); }
            } else { s_scrollSr = null; s_scrollGO = null; }
            // 菜单内右摇杆左右 → 翻页(分页 ScrollSnapPagination,边沿触发一次一页)
            if (!s_battle && s_curCard == null && s_curHandIdx < 0 && s_curBoardIdx < 0) {
                if (DownAct(Act.RLeft, cur)) GamepadApi.PageFlip(s_selRt, TopRank(), -1);
                if (DownAct(Act.RRight, cur)) GamepadApi.PageFlip(s_selRt, TopRank(), +1);
            }
            if (DownAct(Act.Confirm, cur)) DoConfirm();
            if (DownAct(Act.Cancel, cur)) DoCancel();
            s_prev = cur;
        }

        public static string SelfTest()
        {
            var snap = new Snapshot { buttons = new bool[] { false, true, false }, axes = new float[] { 0.9f, -0.9f } };
            int pass = 0, fail = 0;
            Action<bool, string> chk = (c, m) => { if (c) pass++; else { fail++; W("SELFTEST FAIL: " + m); } };
            chk(Active(Btn(1), snap), "btn1"); chk(!Active(Btn(0), snap), "btn0"); chk(!Active(Btn(9), snap), "oob");
            chk(Active(Ax(0, +1), snap), "ax0+"); chk(!Active(Ax(0, -1), snap), "ax0-"); chk(Active(Ax(1, -1), snap), "ax1-");
            return "selftest pass=" + pass + " fail=" + fail;
        }
    }
}
