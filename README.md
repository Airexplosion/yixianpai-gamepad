# 手柄适配

Xbox 手柄操作弈仙牌：左摇杆导航牌面/菜单、B 确认、A 取消，右摇杆摆牌/收回/换位，LB 炼化、RB 换牌、LT 合成、X 突破、RT 长按准备、Back 五行玉瓶、Start 设置、Y 表情。全部走游戏自己的 UI，不发包。插上手柄启用即可。

## 构建

本 mod 需在「弈仙牌 MOD SDK / 加载器」工作区内构建（`.csproj` 用相对路径引用 SDK 项目和本机游戏 DLL，单独 clone 无法直接编译）：

1. 取得 SDK / 加载器工作区（含 `sdk/`、`tools/yx-patch`），把本仓库放到工作区的 `mods/com.yx.gamepad/`。
2. 准备本机 `refs/`：`yx-patch refs --game <游戏目录> --out refs` 从游戏取热更 DLL（厂商版权材料，**不随仓库分发**）。
3. `dotnet build Gamepad.csproj -c Release` → 产物在 `plugins/*.dll`。
4. `yx-patch check plugins/*.dll` 应为 0 error。

产物 DLL 与 `bin/ obj/ plugins/` 不入库（见 `.gitignore`）。
