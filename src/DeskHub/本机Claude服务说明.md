# New B Desk 本机 Claude 服务说明

New B Desk 自带一个本机小服务：把请求转给这台电脑上**已经登录的 claude 命令行**（`claude -p`），把回答还回去。
日历的“智能识别日程”用它；自家别的程序（便签、归档……）也可以直接接，不用另买 API 密钥。

## 安全边界

- 只监听 `127.0.0.1`（写死在代码里），局域网里别的电脑连不上。
- 每个请求都要带口令（设置里自己填的那串字），口令加密保存在本机。
- 带 `Origin` 头的请求（浏览器里的网页发的）一律拒绝；`Host` 不是 `127.0.0.1:端口` / `localhost:端口` 的也拒绝。
- 不给 claude 任何工具（不能读写文件、不能跑命令、不能上网），不留会话记录，只是“问一句答一句”。
- 请求内容不落盘。日志 `%APPDATA%\DeskHub\local-claude.log` 只记时间、接口、模型、字数、图片张数、耗时、结果，不记内容和口令。
- 不开机自启、不建计划任务、不改防火墙：New B Desk 开着、开关打开时才有这个服务。

## 怎么开、怎么停

1. 右下角工具条 → 设置 → 智能识别 → “本机 Claude”。
2. 打开“打开本机 Claude 服务”，端口默认 `17860`，口令自己填一串字，点“保存”。下面会显示“正在运行：http://127.0.0.1:17860”。
3. 想让日历识别用它：点“日历识别改用本机 Claude”，再点“保存”。
4. 停：把开关关掉保存；或者退出 New B Desk。

前提：这台电脑装了 claude 命令行并登录过（在命令行里运行一次 `claude`，按提示登录）。没找到会在设置页上用红字提示。

## 接口

地址 `http://127.0.0.1:17860`（端口以设置里为准）。口令放在 `Authorization: Bearer 口令` 或 `x-api-key: 口令` 头里。
返回都是 UTF-8 JSON。单次调用最多等 100 秒；同时最多跑 2 个，多的排队。

### GET /health（不要口令）

```json
{"ok":true,"service":"New B Desk local Claude","engine":"claude -p","timeout_sec":100}
```

### POST /v1/ask（通用：传提示词，拿文字）

```json
{"prompt": "要问的话", "system": "可选：系统提示", "model": "可选：sonnet / opus / haiku / fable，默认 sonnet", "images": ["可选：base64 或 data:image/png;base64,…"]}
```

返回：

```json
{"text": "回答", "model": "claude-sonnet-5-5"}
```

body 不是 JSON 时，整段当作提示词（UTF-8；Windows 命令行 curl 按系统编码发的中文也认）。

```
curl -s -X POST http://127.0.0.1:17860/v1/ask -H "Authorization: Bearer 口令" -d "明天下午3点开会，整理成日程"
```

### POST /v1/messages（Claude 自己的格式）

和 Anthropic Messages API 一样的请求和返回（`system`、`messages`、文字块、base64 图片块）。日历识别默认走这个。
现成的 Anthropic SDK 把 base_url 设成 `http://127.0.0.1:17860`、api_key 设成口令就能用。

### POST /v1/chat/completions（OpenAI 兼容格式）

和 OpenAI Chat Completions 一样的请求和返回（文字、`image_url` 里的 data URL 图片）。不支持流式，`stream` 会被忽略。
现成的 OpenAI SDK 把 base_url 设成 `http://127.0.0.1:17860/v1`、api_key 设成口令就能用。

### GET /v1/models

返回可选的模型名：`sonnet`、`opus`、`haiku`、`fable`（claude 命令行的别名，对应各自最新版）。

## 出错时

返回 `{"type":"error","error":{"type":"…","message":"…"}}`：

| 状态码 | type | 意思 |
|---|---|---|
| 401 | authentication_error | 口令不对 |
| 403 | forbidden | 网页发来的请求，或者 Host 不对 |
| 400 | invalid_request_error | 请求内容不对（空提示词、模型名不合法、图片给的是网址） |
| 502 | claude_error | claude 命令行报错（比如没登录、额度用完），message 里带它的原话 |
| 503 | claude_not_found | 这台电脑上没找到 claude 命令行 |
| 504 | timeout | 100 秒没回完（已停掉那次调用），或者排队超时 |
