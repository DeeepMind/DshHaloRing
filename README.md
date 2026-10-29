# DSH HaloRing

为 DeepSeek Harness（DSH）打造的 **Windows 11 系统托盘环形状态指示灯**。

环的颜色代表 DSH 当前状态，环的弧长代表任务进度，一眼就能知道 Agent 在做什么。

- **纯本地**：不联网，只读本地状态文件
- **可自定义**：颜色、粗细、主题、动画全部可调
---

## 预览

### 夜间主题（底板 `#202020`）

八个状态横向排列，每个图标都是完整渲染结果：

![夜间主题预览](docs/images/dark/预览拼图.png)

### 日间主题（底板 `#F2F2F2`）

切换主题只改变底板色与轨道色，其余参数不变：

![日间主题预览](docs/images/light/预览拼图.png)



## 特性

### 七种状态，一眼可辨

| 状态 | 颜色 | 表现 |
|---|---|---|
| 运行中 | 绿色 `#10CD52` | 常亮 |
| 等待输入/授权 | 琥珀 `#F5A623` | 慢闪（1.6s） |
| 降级运行 | 橙色 `#FF7A1A` | 快闪（0.42s） |
| 故障/阻塞 | 红色 `#FF3B30` | 常亮 + 周期性双闪 |
| 完成/回合结束 | 绿色 `#10CD52` | 满环 + 慢闪，直到下一状态 |
| 空闲/待机 | 暗绿 `#2F6B45` | 常亮 |
| 离线/失联 | 暗灰 `#5A5A5A` | 常亮 |

多个状态同时成立时按**固定优先级**取一个

```
故障 > 降级 > 等待输入/授权 > 运行中 > 完成 > 空闲 > 离线
```

### 进度提示

环的弧长是**非精确提示**，不承诺准确百分比。进度来自 DSH 的 `step` 事件
（一次 `step` = 一次「模型推理 → 工具调用 → 工具执行完毕」循环）：

```
progress = 25 + 70 × (1 - e^(-已完成step数 / 3.2))  [+8 若正在执行工具]
```

因为**总步数未知**，刻意用渐近曲线而非线性分母，避免长任务卡在某个数字不动的观感。
上限 95，只有回合结束才到 100。进度变化时环弧长**线性平滑增长**，不跳变。


## 快速开始

### 方式 A：安装包（推荐普通用户）

从 [Releases](../../releases) 下载 `DshHaloRing-Setup-x.y.z.exe`，双击安装。

- **自带 .NET 运行时**，无需另装任何依赖
- 装到当前用户目录，**不需要管理员权限**
- 自动创建开始菜单项与卸载入口
- 安装时可勾选「开机自启动」

安装完成后照第 2 步配置数据源即可。

### 方式 B：从源码构建

**前置要求**：

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- DeepSeek Harness（桌面版）

```powershell
cd DshHaloRing
dotnet build -c Release
.\bin\Release\net8.0-windows\DshHaloRing.exe
```

### 1. 安装 DSH 状态桥插件

托盘程序靠一个 DSH 宿主插件获取状态。在 DSH 所在终端执行：

```powershell
dsh plugin --profile desktop add "file:<仓库路径>/DshHaloRing/plugin/dsh-haloring-status"
```

> **重要**：插件安装后会复制到 `~/.dsh/profiles/<profile>/node_modules/`，
> DSH 实际从这里加载。修改插件源码后，两处都要同步，否则改动不生效。

安装后**重启 DSH 一次**加载插件。验证：`%USERPROFILE%\.dsh\dsh-status.txt`
应出现且每秒更新。

### 2. 配置数据源

首次运行默认用演示驱动。右键托盘图标 →「外观设置」→「状态源」→
选 **DSH 状态桥（推荐）**，路径留空即自动指向 `%USERPROFILE%\.dsh\dsh-status.txt`。

托盘交互：

- **左键单击** → 打开外观设置
- **右键** → 菜单（进度显示 / 外观设置 / 恢复默认外观 / 开机自启动 / 退出）

---

## 打包发布

项目自带一键构建脚本：

```powershell
# 完整构建（exe + 安装包）
.\build-release.ps1

# 只构建可运行文件，跳过安装包
.\build-release.ps1 -SkipInstaller
```

产物：

| 路径 | 说明 |
|---|---|
| `dist\DshHaloRing-Setup-<version>.exe` | Inno Setup 安装包，约 47 MB |
| `publish-dir\` | 自包含目录版，可直接运行或绿色部署 |

**编译安装包需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)**：

```powershell
winget install JRSoftware.InnoSetup
```

> **为什么不用 `PublishSingleFile`**
>
> 单文件版启动时需把内嵌程序解压到 `%TEMP%\.net`。
> 在某些受限环境（临时目录 ACL 异常、只读用户配置）下会因
> 「Failed to create default extraction directory」而启动失败。
> 目录形式没有这个依赖，更可靠——安装包本身已经把 245 个文件压成 47 MB。


---

## 数据链路

```
DSH 宿主
  └─ 插件 dsh-haloring-status（每 300ms 轮询会话事件）
       ├─ 跟踪 turn/start、step/start、step/end、tool/call、tool/result
       ├─ 计算进度、合成状态优先级
       └─ 写 ~/.dsh/dsh-status.txt（每 250ms~1s）

DshHaloRing 托盘程序（每 300ms 轮询）
  ├─ DshStatusFileSource 解析 key=value 格式
  ├─ StateAggregator 按优先级合成最终状态
  ├─ IconAnimationLoop 30fps 推进动画 + 线性插值进度
  └─ RingIconRenderer 逐帧渲染 → 多尺寸 ico → 托盘图标
```

### 状态文件格式

```ini
version=1
state=running          # idle / running / attention / degraded / error / completed / offline
priority=2
updated=1793210745187  # Unix 毫秒，读取方据此判断心跳
pid=12716
detail=Agent 运行中（turn 35）
progress=44            # 0..100，非精确提示
attention=0
approvals=0
turns=1
sessions=1
completed=0
degraded=0
error=0
```

超过心跳超时（默认 8 秒）无更新即判为离线。


## 许可证

[GPL-3.0](LICENSE)

你可以自由使用、修改和分发，但衍生作品必须以相同许可证开源。
