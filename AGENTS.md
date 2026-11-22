# QuickLook 项目约定

## 修改与验证

- 保留 QuickLook 的资源管理器选择跟随与默认不抢焦点的行为；涉及共享控件时检查其余插件的兼容性。
- 涉及界面文字时遵循 `.github/copilot-instructions.md` 的多语言约定。
- 完成功能新增、修复或其他影响程序行为的代码修改后，在本机同时生成个人版 **Release MSI 和完整便携 ZIP** 作为交付，无需再次询问是否构建。
- 仅文档修改通常不必重新构建；修改构建、打包流程或用户明确要求时必须执行。

## 本地 Release 安装包与便携包

统一从仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\build-release.ps1 -WithInstaller
```

- 默认生成适用于 Windows x64 的包，包含宿主、解决方案内全部内置插件、x86/x64 原生组件及运行依赖；不把局部 Debug 验证目录当作完整发行版。
- 构建脚本负责发现本机 Visual Studio/MSBuild 和 Windows SDK、还原依赖、以 Release 构建、整理便携目录、验证并压缩。无需先生成 MSI，也不依赖 7-Zip。
- 仅需便携包时可省略 `-WithInstaller`。安装包复用同一次构建，使用固定的个人版升级标识和每用户安装目录，排除 `portable.lock`。MSI 工具按需恢复到 `Build/Tools`。
- 个人 MSI 产品版本从 `QuickLook.Installer/ForkVersion.txt` 起算，该文件上次修改后的主线提交数加在第三段。正式包应来自准备交付的提交。
- 原生组件需要 MSVC v145 的 x86/x64 工具与 ATL；工具缺失时明确报告原因。不要静默略过失败项目、使用旧 DLL 补齐缺失组件，或把失败构建报告为成功。
- ZIP 必须包含 `portable.lock`、许可证、配置说明、记录提交与构建信息的 `build-info.json`，以及所有内置插件。不能打包用户的 `UserData`、测试夹具、调试符号或临时构建文件。
- 产物保存在 `Build/Releases/`，文件名包含版本、提交标识及 `win-x64`，同时生成 SHA-256 校验文件；保留已有的 Release ZIP。
- 无版本标签的 Fork 允许使用 `0.0.0` 本地版本，但必须保留提交标识；不能把 Git 哈希写入数值型 `AssemblyVersion`。
- 构建仅能清理仓库 `Build/` 内脚本拥有的构建/暂存目录；删除前验证解析后的绝对路径，不得跟随目录链接清理其他位置。
- 完成后报告生成的 MSI 和 ZIP 路径、提交标识和验证结果。完整打包不等于完成所有界面实测，须如实说明验证范围。

## 提交与执行顺序

- 不因本约定自动提交或推送。用户明确要求提交时，提交当前任务相关源码与文档，排除 `Build/`、`GitVersion.cs` 等生成产物。
- 用户要求“提交后触发”时，先提交已检查的构建流程，再运行脚本，使包内记录对应提交。若实际构建暴露问题，修复并验证；本次已授权提交的任务应补充提交修复，再生成与最终提交对应的包。
- 默认在本机生成安装包与便携包；不自动安装、替换或启动当前商店版，不自动发布 GitHub Release，也不推送远端。禁止任何形式的强制推送。
