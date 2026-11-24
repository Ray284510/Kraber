# 发布副本核验 · 2026-11-24

本次在 Windows x64 的独立发布副本运行下列检查，使用与项目对应的本机 Python 3.12 环境和 NX 12.0.2.9 SDK。没有启动 NX、调用原生命令、重放任务或操作生产数据库。图形界面、PRT 保存重开及 STEP 回读见历史证据。

| 检查 | 本次结果 |
|---|---|
| Python stdio 服务 | initialize 成功，tools/list 实际返回 41 个不同名称，原生调用 0 次。[协议版本和完整工具表](verification/mcp-tool-inventory.json) |
| Loader 生成与 C# 编译 | PASS。按构建锁核对输入，生成并编译 NX12_Loader.dll；编译器报告旧 NX API 弃用警告。SDK 和产物 DLL 保留在本机 |
| NXOpen Journal 离线编译 | 三个 VB Journal 通过对应 NX12 SDK 编译。脱敏版在 GUI 中重跑为 NOT_RUN |
| mechanism_agent | 39 个离线测试通过 |
| 几何积分、投影、修剪、P11 几何门槛、检查点及 P12 批次 | 112 个离线测试通过 |
| P13 continuation 的模拟客户端、临时库用例 | 19 个通过 |
| reviewed holds 的离线测试 | 6 个通过 |
| tests 目录整体，普通模式 | 53 个中 50 个通过、3 个跳过。跳过的 2 个需要私有真实裁决档案，另 1 个需要原生归档目录 |
| 单独复核历史裁决测试，Python -O | 23 个中 21 个通过、2 个真实档案用例跳过；合成签发、回滚及其他 UNKNOWN 均按预期处理 |

## 复测命令

在 `src/nx-ai-lab` 目录准备依赖后运行。模拟测试的临时数据写在本 checkout 的运行目录。

```powershell
New-Item -ItemType Directory -Force -Path tmp,logs,models | Out-Null
$python = './.venv/Scripts/python.exe'
& $python -B bench_list_tools.py
& $python -B -m unittest discover -s mechanism_agent -t . -q
& $python -B -m unittest ai_studio.geometry.test_volume_integrator ai_studio.geometry.test_projection ai_studio.geometry.test_trimmed_planar ai_studio.test_geometry_gate_p11 ai_studio.test_checkpoint_receipt ai_studio.test_batch_geometry_p12 -q
& $python -B -m unittest ai_studio.test_p13_continuation ai_studio.test_reviewed_holds -q
& $python -B -m unittest discover -s tests -q
& $python -B -O -m unittest discover -s tests -p test_p13_legacy_adjudication.py -q
```

## 文件检查

根清单覆盖本次提交除清单自身外的全部文件，按提交字节计算。另对 [outputs 清单](../outputs/SHA256SUMS.txt) 中的各项做独立核对。

提交前检查了路径和文件内容中的本机登录名、主机名、个人目录、硬编码本机安装位置、内网地址、邮件、常见令牌、私钥和明文凭据格式。PNG 保留经过检查的图像数据；归档 STEP 核验文本头尾、字符串元数据及 SHA-256。来源包的支付记录、许可证、数据库与原始私有日志留在本机。
