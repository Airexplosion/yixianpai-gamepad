using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Proto;   // CardPosition

namespace YxGamepad
{
    /// <summary>
    /// 一个导航目标里的卡牌信息，回传给导航层用 mod 安全类型表示：
    /// <see cref="Rt"/> 用于几何/准星；<see cref="Card"/> 是不透明卡牌句柄（实为 CardItem，导航层只按引用相等比较、原样回传，不解引用）；
    /// <see cref="HandIdx"/> / <see cref="BoardIdx"/> 是手牌 / 牌桌（玉瓶开着时为瓶格）下标，无则 -1。
    /// </summary>
    public sealed class PadCardTarget
    {
        public RectTransform Rt;
        public object Card;
        public int HandIdx = -1;
        public int BoardIdx = -1;
    }

    /// <summary>
    /// 手柄 mod 的游戏绑定层：所有对游戏热更类型（CardPanel / CardItem / BattlePanel / 各面板 …）的读写都集中在这里，
    /// 对外只暴露 mod 安全类型（RectTransform / object / int / bool / <see cref="PadCardTarget"/>）。这样导航主体（Gamepad.dll）
    /// 不引用任何游戏 DLL，游戏更新只会打到本程序集一处。行为逐一对照原独立版 Gamepad.cs 移植，全部走游戏自己的 UI，不发包改状态。
    /// </summary>
    public static class GamepadApi
    {
        static Action<string> s_log;
        public static void SetLogger(Action<string> log) { s_log = log; }
        static void W(string m) { try { if (s_log != null) s_log(m); } catch (Exception) { } }

        // ── 面板解析 ──────────────────────────────────────────────
        static CardPanel CP() { var bp = ILRPanelBase.FindILRPanel<BattlePanel>(); return bp == null ? null : bp.FindILRSubPanel<CardPanel>(); }
        static BattlePanel BP() { return ILRPanelBase.FindILRPanel<BattlePanel>(); }
        static Talent199Panel YuPing()
        {
            try {
                var bp = BP(); if (bp == null) return null;
                var p = bp.FindILRSubPanel<Talent199Panel>(); if (p == null) return null;
                var go = p.gameObject;
                return (go != null && go.activeInHierarchy) ? p : null;
            } catch (Exception) { return null; }
        }
        static List<CardGridCunQuItem> YuPingGrids()
        {
            try { var p = YuPing(); if (p == null) return null; var c = p.cardGridContainer; return c == null ? null : c.cardGrids; }
            catch (Exception) { return null; }
        }
        static bool CardsOperable(CardPanel cp)
        {
            try { var cg = cp.GetCanvasGroup(); return cg == null || cg.blocksRaycasts; } catch (Exception) { return true; }
        }

        // ── 状态查询（导航层每帧问）──────────────────────────────
        /// <summary>当前是否在有 CardPanel 的对局界面（导航层据此切「战斗内轻量采集」模式）。</summary>
        public static bool HasCardPanel() { return CP() != null; }
        /// <summary>卡牌此刻可操作吗（摆牌阶段 true；斗法/发牌动画 false）。</summary>
        public static bool CardsOperable() { var cp = CP(); return cp != null && CardsOperable(cp); }
        /// <summary>五行玉瓶面板开着吗。</summary>
        public static bool YuPingOpen() { return YuPing() != null; }

        // ── 目标枚举（返回 mod 安全的 PadCardTarget，导航层再算屏幕矩形/入列）──
        /// <summary>手牌目标（Rt=可拖动层或本体，Card=句柄，HandIdx=下标）。</summary>
        public static List<PadCardTarget> HandCards()
        {
            var outList = new List<PadCardTarget>();
            try {
                var cp = CP(); if (cp == null) return outList;
                var h = cp.GetHandCards(); if (h == null) return outList;
                for (int i = 0; i < h.Count; i++) {
                    var card = h[i]; if (card == null) continue;
                    var rt = card.movableRT != null ? card.movableRT : card.transform as RectTransform;
                    outList.Add(new PadCardTarget { Rt = rt, Card = card, HandIdx = i, BoardIdx = -1 });
                }
            } catch (Exception) { }
            return outList;
        }
        /// <summary>牌桌格目标（占用格 Rt=牌的可拖动层、Card=牌；空格 Rt=格子本体、Card=null；BoardIdx=下标）。</summary>
        public static List<PadCardTarget> BoardGrids()
        {
            var outList = new List<PadCardTarget>();
            try {
                var cp = CP(); if (cp == null) return outList;
                var g = cp.GetCardGrids(); if (g == null) return outList;
                for (int i = 0; i < g.Count; i++) {
                    var gr = g[i]; if (gr == null) continue;
                    var bc = gr.GetCard();
                    var rt = (bc != null && bc.movableRT != null) ? bc.movableRT : gr.transform as RectTransform;
                    outList.Add(new PadCardTarget { Rt = rt, Card = bc, HandIdx = -1, BoardIdx = i });
                }
            } catch (Exception) { }
            return outList;
        }
        /// <summary>五行玉瓶 3 个存取格目标；玉瓶没开返回 null（导航层据此决定采集瓶格还是牌桌）。</summary>
        public static List<PadCardTarget> YuPingTargets()
        {
            var yg = YuPingGrids(); if (yg == null) return null;
            var outList = new List<PadCardTarget>();
            try {
                for (int i = 0; i < yg.Count; i++) {
                    var gr = yg[i]; if (gr == null) continue;
                    var bc = gr.GetCard();
                    var rt = (bc != null && bc.movableRT != null) ? bc.movableRT : gr.rt;
                    outList.Add(new PadCardTarget { Rt = rt, Card = bc, HandIdx = -1, BoardIdx = i });
                }
            } catch (Exception) { }
            return outList;
        }
        /// <summary>战斗内的长按触发器（头像/玩家信息项，点击切看对手牌局）；以 MonoBehaviour 回传供导航层入列。</summary>
        public static List<MonoBehaviour> LongPressTriggers()
        {
            var outList = new List<MonoBehaviour>();
            try {
                var lp = UnityEngine.Object.FindObjectsOfType<LongPressedEventTrigger>();
                if (lp != null) for (int i = 0; i < lp.Length; i++) { var l = lp[i]; if (l != null) outList.Add(l); }
            } catch (Exception) { }
            return outList;
        }

        // ── 悬浮（游戏自带 OnPointerEnter/Exit 放大，反射调私有方法；只对托管 CardItem 类型，安全）──
        static object s_hoverCard; static PointerEventData s_ped;
        static System.Reflection.MethodInfo s_miEnter, s_miExit; static bool s_miTried;
        static PointerEventData PED() { if (s_ped == null) { try { s_ped = new PointerEventData(EventSystem.current); } catch (Exception) { } } return s_ped; }
        static void ResolveHover()
        {
            if (s_miTried) return; s_miTried = true;
            try {
                var t = typeof(CardItem);
                var fl = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                s_miEnter = t.GetMethod("OnPointerEnter", fl); s_miExit = t.GetMethod("OnPointerExit", fl);
            } catch (Exception) { }
        }
        /// <summary>把悬浮放大切到这张卡（句柄由枚举给出）；传 null = 取消当前悬浮。仅句柄变化时触发一次。</summary>
        public static void HoverCard(object card)
        {
            if (s_hoverCard == card) return;
            ResolveHover(); var ped = PED();
            if (s_hoverCard != null && s_miExit != null) { try { s_miExit.Invoke(s_hoverCard, new object[] { ped }); } catch (Exception) { } }
            s_hoverCard = card;
            if (card != null && s_miEnter != null) { try { s_miEnter.Invoke(card, new object[] { ped }); } catch (Exception) { } }
        }

        // ── 悬浮提示（仙命/关键词）自动隐藏抑制；手柄不动真鼠标，反射 dispose 掉 m_EveryUpdate ──
        static void KillEveryUpdate(object panel)
        {
            try {
                if (panel == null) return;
                var f = panel.GetType().GetField("m_EveryUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null) return;
                var d = f.GetValue(panel) as IDisposable;
                if (d != null) d.Dispose();
            } catch (Exception) { }
        }
        /// <summary>进入新元素后禁掉悬浮提示的自动隐藏（不动鼠标也不消失）。</summary>
        public static void SuppressTooltips()
        {
            try {
                var tip = ILRPanelBase.FindILRPanel<TooltipsPanel>(); if (tip == null) return;
                KillEveryUpdate(tip.FindILRSubPanel<TalentDescriptionPanel>());
                KillEveryUpdate(tip.FindILRSubPanel<KeywordDetailPanel>());
            } catch (Exception) { }
        }
        /// <summary>离开旧元素时主动关掉之前撑着的悬浮提示。</summary>
        public static void HideTooltips()
        {
            try {
                var tip = ILRPanelBase.FindILRPanel<TooltipsPanel>(); if (tip == null) return;
                var td = tip.FindILRSubPanel<TalentDescriptionPanel>(); if (td != null && td.panel != null && td.panel.isShow) td.Hide();
                var kd = tip.FindILRSubPanel<KeywordDetailPanel>(); if (kd != null && kd.panel != null && kd.panel.isShow) kd.Hide();
            } catch (Exception) { }
        }
        /// <summary>× 取消时优先关掉「无关闭键的悬浮卡」（好友玩家详情 / 角色仙命列表）；关掉了返回 true。</summary>
        public static bool HideFloatingPanels()
        {
            try {
                var fp = ILRPanelBase.FindILRPanel<FriendPanel>();
                if (fp != null) { var fd = fp.FindILRSubPanel<FriendDetailPanel>(); if (fd != null && fd.panel != null && fd.panel.isShow) { fd.Hide(); return true; } }
            } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<TooltipsPanel>())) return true; } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<LobbyPanel>())) return true; } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<BattlePanel>())) return true; } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<RewardCharacterSelectPanel>())) return true; } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<FriendPanel>())) return true; } catch (Exception) { }
            try { if (HideTalentListIn(ILRPanelBase.FindILRPanel<SettingsPanel>())) return true; } catch (Exception) { }
            return false;
        }
        static bool HideTalentListIn(ILRPanelBase p)
        {
            try {
                if (p == null) return false;
                var t = p.FindILRSubPanel<TalentListPanel>();
                if (t != null && t.panel != null && t.panel.isShow) { t.Hide(); return true; }
            } catch (Exception) { }
            return false;
        }

        // ── 玉瓶存取（内部：返回 true = 已被玉瓶消化，别再落到牌桌）──
        static bool YuPingPut(CardPanel cp, int handIdx, out int reselectBoard)
        {
            reselectBoard = -1;
            var yg = YuPingGrids(); if (yg == null) return false;
            try {
                if (handIdx < 0) return true;
                var h = cp.GetHandCards(); if (h == null || handIdx >= h.Count) return true;
                for (int i = 0; i < yg.Count; i++) {
                    var gr = yg[i]; if (gr == null || gr.GetCard() != null) continue;
                    gr.MoveCardToGrid(h[handIdx]); reselectBoard = i; return true;
                }
            } catch (Exception ex) { W("yuping put err " + ex.Message); }
            return true;
        }
        static bool YuPingTake(CardPanel cp, int boardIdx)
        {
            var yg = YuPingGrids(); if (yg == null) return false;
            try {
                if (boardIdx < 0 || boardIdx >= yg.Count) return true;
                var gr = yg[boardIdx]; if (gr == null) return true;
                var c = gr.GetCard(); if (c != null) CardGridCunQuItem.MoveCardToHand(c, gr);
            } catch (Exception ex) { W("yuping take err " + ex.Message); }
            return true;
        }
        static bool YuPingSwap(int boardIdx, int dir, out int reselectBoard)
        {
            reselectBoard = -1;
            var yg = YuPingGrids(); if (yg == null) return false;
            try {
                if (boardIdx < 0 || boardIdx >= yg.Count) return true;
                int j = boardIdx + dir; if (j < 0 || j >= yg.Count) return true;
                var src = yg[boardIdx]; var dst = yg[j];
                if (src == null || dst == null) return true;
                var c = src.GetCard(); if (c == null) return true;
                dst.MoveCardToGrid(c); reselectBoard = j;
            } catch (Exception ex) { W("yuping swap err " + ex.Message); }
            return true;
        }

        // ── 牌位动作（返回要重选的 boardIdx，-1 = 不重选；玉瓶开着时自动走玉瓶）──
        /// <summary>摆牌：玉瓶开着放进第一个空瓶格，否则放进第一个空牌桌格。返回落点 boardIdx（-1 未动）。</summary>
        public static int Place(int handIdx)
        {
            var cp = CP(); if (cp == null) return -1;
            int rs; if (YuPingPut(cp, handIdx, out rs)) return rs;
            try {
                if (handIdx < 0) return -1;
                var h = cp.GetHandCards(); var g = cp.GetCardGrids();
                if (h == null || g == null || handIdx >= h.Count) return -1;
                CardGrid e = null; int ei = -1;
                for (int i = 0; i < g.Count; i++) { if (g[i] != null && g[i].unlocked && g[i].GetCard() == null) { e = g[i]; ei = i; break; } }
                if (e != null) { cp.MoveToGrid(h[handIdx], e); return ei; }
            } catch (Exception ex) { W("place err " + ex.Message); }
            return -1;
        }
        /// <summary>收回：玉瓶开着从瓶格取回手牌，否则把牌桌格的牌收回手牌。</summary>
        public static void Evict(int boardIdx)
        {
            var cp = CP(); if (cp == null) return;
            if (YuPingTake(cp, boardIdx)) return;
            try {
                if (boardIdx < 0) return;
                var g = cp.GetCardGrids(); if (g == null || boardIdx >= g.Count) return;
                var c = g[boardIdx].GetCard(); if (c != null) cp.MoveToHand(c);
            } catch (Exception ex) { W("evict err " + ex.Message); }
        }
        /// <summary>换位：玉瓶开着换瓶格，否则换相邻牌桌格。返回落点 boardIdx（-1 未动）。</summary>
        public static int Swap(int boardIdx, int dir)
        {
            var cp = CP(); if (cp == null) return -1;
            int rs; if (YuPingSwap(boardIdx, dir, out rs)) return rs;
            try {
                if (boardIdx < 0) return -1;
                var g = cp.GetCardGrids(); if (g == null) return -1;
                int j = boardIdx + dir; if (boardIdx >= g.Count || j < 0 || j >= g.Count) return -1;
                if (g[j] == null || !g[j].unlocked) return -1;
                var c = g[boardIdx].GetCard(); if (c == null) return -1;
                cp.MoveToGrid(c, g[j]); return j;
            } catch (Exception ex) { W("swap err " + ex.Message); }
            return -1;
        }
        /// <summary>炼化选中手牌。</summary>
        public static void Refine(int handIdx)
        {
            try { var cp = CP(); if (cp == null || handIdx < 0) return; var h = cp.GetHandCards(); if (h != null && handIdx < h.Count) cp.refineArea.RefineCard(h[handIdx]); }
            catch (Exception ex) { W("refine err " + ex.Message); }
        }
        /// <summary>换牌：换掉选中手牌，返回该牌句柄（换牌走服务器异步、原地复用同对象）供导航层按对象钉住焦点。</summary>
        public static object Replace(int handIdx)
        {
            try {
                var cp = CP(); if (cp == null || handIdx < 0) return null;
                var h = cp.GetHandCards(); if (h == null || handIdx >= h.Count) return null;
                var card = h[handIdx]; cp.replaceArea.ReplaceCard(card); return card;
            } catch (Exception ex) { W("replace err " + ex.Message); return null; }
        }
        /// <summary>合成：把选中卡（句柄）与同名可升级的另一张合并（手牌内升级 / 与场上互移）。</summary>
        public static void Merge(object selectedCard)
        {
            try {
                var cp = CP(); if (cp == null) return;
                var card = selectedCard as CardItem; if (card == null || card.cardConfig.noUpgrade) return;
                var partner = FindMergePartner(cp, card.cardInfo.id, card.cardInfo.position, card.cardInfo.index);
                if (partner == null) return;
                bool hHand = card.cardInfo.position == CardPosition.Hand;
                if (hHand && partner.cardInfo.position == CardPosition.Hand) cp.TryUpgradeHandCard(card, partner);
                else if (partner.cardInfo.position == CardPosition.Used) cp.MoveToGrid(card, cp.GetCardGrids()[partner.cardInfo.index]);
                else cp.MoveToGrid(partner, cp.GetCardGrids()[card.cardInfo.index]);
            } catch (Exception ex) { W("merge err " + ex.Message); }
        }
        static CardItem FindMergePartner(CardPanel cp, int id, CardPosition selfPos, int selfIdx)
        {
            try {
                var used = cp.GetUsedCards();
                if (used != null) for (int i = 0; i < used.Count; i++) { var c = used[i]; if (c == null) continue; if (c.cardInfo.id != id || c.cardConfig.noUpgrade) continue; if (c.cardInfo.position == selfPos && c.cardInfo.index == selfIdx) continue; return c; }
                var hand = cp.GetHandCards();
                if (hand != null) for (int i = 0; i < hand.Count; i++) { var c = hand[i]; if (c == null) continue; if (c.cardInfo.id != id || c.cardConfig.noUpgrade) continue; if (c.cardInfo.position == selfPos && c.cardInfo.index == selfIdx) continue; return c; }
            } catch (Exception) { }
            return null;
        }
        /// <summary>突破（攒够修为时游戏才有；不可用性由导航层按 "LevelUpBtn" 判断）。</summary>
        public static void Breakthrough()
        {
            try { var bm = BattleManager.Instance; if (bm != null) bm.PendingTalentReq(); } catch (Exception ex) { W("bt err " + ex.Message); }
        }
        /// <summary>准备（点游戏自己的准备按钮）。</summary>
        public static void Ready()
        {
            try { var bp = BP(); if (bp != null && bp.readyLayer != null) bp.readyLayer.PressReadyButton(); } catch (Exception ex) { W("ready err " + ex.Message); }
        }
        // 五行玉瓶：调游戏自己的点击入口（私有无参，反射；BattlePanel 是托管类型，安全）
        static System.Reflection.MethodInfo s_miYuPing; static bool s_miYuPingTried;
        /// <summary>开/切 五行玉瓶面板（角色立绘相机渲染、导航够不到，故给专用键直接调游戏点击入口）。</summary>
        public static void OpenYuPing()
        {
            try {
                var bp = BP(); if (bp == null) return;
                if (!s_miYuPingTried) {
                    s_miYuPingTried = true;
                    try { s_miYuPing = typeof(BattlePanel).GetMethod("OnWuXingYuPingClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); }
                    catch (Exception e) { W("yuping resolve err " + e.Message); }
                }
                if (s_miYuPing == null) { W("yuping: 未找到 OnWuXingYuPingClick"); return; }
                s_miYuPing.Invoke(bp, null);
            } catch (Exception ex) { W("yuping err " + ex.Message); }
        }
        /// <summary>打开设置界面。</summary>
        public static void OpenSettings()
        {
            try { ILRPanelBase.ShowILRPanelAsync<SettingsPanel>(); } catch (Exception ex) { W("settings err " + ex.Message); }
        }
        /// <summary>游戏设置面板的根 RectTransform（供导航层往它底部挂「手柄设置」按钮，随面板显示/隐藏）；实例不存在返回 null。</summary>
        public static RectTransform SettingsPanelRoot()
        {
            try {
                var p = ILRPanelBase.FindILRPanel<SettingsPanel>();
                if (p == null) return null;
                var go = p.gameObject; if (go == null) return null;
                return go.transform as RectTransform;
            } catch (Exception) { return null; }
        }
        /// <summary>切换表情面板（复刻左键点己方头像的逻辑）。</summary>
        public static void ToggleEmoji()
        {
            try {
                var bp = BP(); if (bp == null) return;
                var emoji = bp.FindILRSubPanel<BattleEmojiSelectionPanel>(); if (emoji == null) return;
                if (emoji.panel != null && emoji.panel.isShow) { emoji.Hide(); return; }
                int charId = -1; try { charId = BattleManager.Instance.currentGameStatus.GetMainPlayerData().characterId; } catch (Exception) { }
                if (charId < 0) return;
                RectTransform anchor = null;
                try { if (bp.readyLayer != null && bp.readyLayer.playerSelfInfoItem != null) anchor = bp.readyLayer.playerSelfInfoItem.transform as RectTransform; } catch (Exception) { }
                emoji.ShowBox(anchor, charId, TooltipBoxAlignment.Top);
            } catch (Exception ex) { W("emoji err " + ex.Message); }
        }

        // ── 分页（ScrollSnapPagination 是游戏 UI 类型）──
        static int LayerRank(RectTransform rt)
        {
            try {
                for (Transform t = rt; t != null; t = t.parent) {
                    if (t.parent == null || t.parent.gameObject.name != "RootPanel") continue;
                    var n = t.gameObject.name;
                    if (n == "BlockLayer") return 4;
                    if (n == "TopLayer") return 3;
                    if (n == "PopupLayer") return 2;
                    if (n == "NormalLayer") return 1;
                    if (n == "FixedLayer") return 0;
                    return 1;
                }
            } catch (Exception) { }
            return 1;
        }
        static ScrollSnapPagination FindPagination(RectTransform selected, int topRank)
        {
            try {
                if (selected != null) { var p = selected.GetComponentInParent(typeof(ScrollSnapPagination)) as ScrollSnapPagination; if (p != null && p.isActiveAndEnabled) return p; }
                var ps = UnityEngine.Object.FindObjectsOfType<ScrollSnapPagination>();
                if (ps != null) for (int i = 0; i < ps.Length; i++) { var p = ps[i]; if (p == null || !p.isActiveAndEnabled) continue; if (topRank >= 0 && LayerRank(p.transform as RectTransform) != topRank) continue; return p; }
            } catch (Exception) { }
            return null;
        }
        /// <summary>右摇杆左右翻页（分页组件）。selected = 当前选中项；topRank = 当前最高层级（&lt;0 不限层）。翻了返回 true。</summary>
        public static bool PageFlip(RectTransform selected, int topRank, int delta)
        {
            try {
                var pag = FindPagination(selected, topRank); if (pag == null) return false;
                int cur = pag.currentPage, cnt = pag.pageCount;
                int next = cur + delta; if (next < 0) next = 0; if (cnt > 0 && next > cnt - 1) next = cnt - 1;
                if (next != cur) { pag.JumpToPage(next); return true; }
            } catch (Exception ex) { W("pageflip err " + ex.Message); }
            return false;
        }
    }
}
