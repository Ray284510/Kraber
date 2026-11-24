# NXOpen VB.NET 示例

在启动 NX 之前设置 NX_EXAMPLE_DIR 为一个已存在的可写绝对目录。在 NX 12 的 Journal 播放入口选择 VB 文件。

- [FacetedStlImport.vb](FacetedStlImport.vb) 在空会话导入该目录下的 input.stl（单体二进制 STL，毫米），枚举三角面并实测跨度。输出 faceted.prt 和 faceted.log，保存并重开复测。
- [DesktopIndicator.vb](DesktopIndicator.vb) 在空会话建立独立桌面指示器，检查表达式更新、原生实体、离散位置、装配引用和零件对干涉，输出 desk-indicator/native.log。总装入口为 desk-indicator/parts/00_Display_Stand.prt。
- [DesktopIndicatorInspection.vb](DesktopIndicatorInspection.vb) 在桌面总装打开后运行，记录组件位置及面几何，生成临时截面图并撤销视图操作。输出 desk-indicator/service.log。

使用新目录重新运行构建或导入。验收以日志 outcome 为准，截图错误单独记录。三份源文件分别以 .NET Framework VB 编译器及 NX 12.0.2.9 的 NXOpen.dll、NXOpen.UF.dll、NXOpen.Utilities.dll 离线编译通过。桌面示例的截图 API 和选择规则属性产生弃用警告。公开版通过环境变量解析输入目录，移除了历史路径、进程编号和本地会话日志约束。公开版 NX 图形会话运行状态为 NOT_RUN。