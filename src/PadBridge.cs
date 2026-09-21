namespace YxGamepad
{
    /// <summary>
    /// agent 的喂入入口。frida agent 在游戏进程内每帧读 XInput，格式化成 19 字段串
    /// （"b0,…,b10,a0,…,a7"，见 Gamepad.SetPad），经 onMain 在 Unity 主线程上调 <see cref="Feed"/>。
    /// 这里只把最新一帧存进静态；真正的解析 + 每帧动作由 <see cref="GamepadMod"/> 的 OnUpdate
    /// 取 <see cref="Latest"/> 交给 Gamepad.SetPad + Gamepad.Tick。
    ///
    /// 线程：Feed（agent onMain）与 Latest 的读取（加载器 Pump 的 mod 更新阶段）都在 Unity 主线程，
    /// 单线程串行、互不重入，所以一个静态串足够，不需要锁或队列。
    /// agent 只认「类型全名 + 方法名」这一稳定契约（管理器经 padStart 传入），不与本 mod 的内部实现耦合。
    /// </summary>
    public static class PadBridge
    {
        static string s_latest = "";
        static int s_feeds;

        /// <summary>agent 每帧调：存下最新快照串。返回 "ok" 供 agent 侧确认调用成功。</summary>
        public static string Feed(string s)
        {
            s_latest = s != null ? s : "";
            s_feeds++;
            return "ok";
        }

        /// <summary>最近一次喂入的快照串；还没喂过时为空串。</summary>
        public static string Latest { get { return s_latest; } }

        /// <summary>诊断：agent 累计调用 Feed 的次数（判断 invoke 是否真的进了热更域）。</summary>
        public static int Feeds { get { return s_feeds; } }

        // 手柄信息：agent 连上/换手柄时推一次，格式 "名字|来源|槽位"（来源 xinput/winmm；没连为空）。
        static string s_info = "";

        /// <summary>agent 推手柄信息："名字|来源|槽位"。返回 "ok"。</summary>
        public static string Info(string s)
        {
            s_info = s != null ? s : "";
            return "ok";
        }

        /// <summary>当前手柄名；没连为空串。</summary>
        public static string Name { get { return Part(0); } }
        /// <summary>输入来源：xinput / winmm；没连为空串。</summary>
        public static string Src { get { return Part(1); } }
        /// <summary>手柄槽位号（文本）；没连为空串。</summary>
        public static string SlotText { get { return Part(2); } }
        /// <summary>有没有检测到手柄。</summary>
        public static bool Connected { get { return s_info.Length > 0; } }

        static string Part(int i)
        {
            if (s_info.Length == 0) return "";
            string[] p = s_info.Split('|');
            return i < p.Length ? p[i] : "";
        }
    }
}
