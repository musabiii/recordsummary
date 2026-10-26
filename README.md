# RecordSummary

Приложение в трее: замечает звонок (Zoom, Teams, Telegram, Discord, браузер…), записывает его двумя дорожками
и после звонка делает `transcript.md` и `summary.md`.

- **Детектор** — по реестру Windows видит, какое приложение держит микрофон (`CallDetector.cs`).
- **Запись** — `mic.wav` (вы) и `system.wav` (собеседники, WASAPI loopback). Раздельные дорожки = "Я / Собеседник" без диаризации.
- **Транскрипция** — whisper.cpp (Whisper.net, Vulkan) по кускам речи из Silero VAD.
- **Саммари** — локальная gguf-модель (LLamaSharp) или любой OpenAI-совместимый API.

Записи: `Документы\RecordSummary\<дата> <приложение>\`.

## Настройки (`config.json` рядом с exe)

| Ключ | Что делает |
|---|---|
| `AutoRecord` | `true` — писать сразу, `false` — спрашивать уведомлением |
| `WhisperModel`, `Language` | модель whisper (скачается сама, ~550 МБ) и язык |
| `SummaryProvider` | `local`, `api` или `off` |
| `LocalModel`, `LocalModelUrl`, `LocalContextSize` | gguf для саммари (по умолчанию Qwen2.5-7B Q4, ~4.7 ГБ, скачается сама) |
| `ApiBaseUrl`, `ApiKey`, `ApiModel` | OpenRouter по умолчанию; ключ можно не писать, а задать `OPENROUTER_API_KEY` |

Тот же `api` подходит для локальных серверов: Ollama — `http://localhost:11434/v1`, LM Studio — `http://localhost:1234/v1`.

Длинный звонок, который не влезает в контекст модели, саммаризируется по частям: заметки по фрагментам, затем итог.

## Проверка без звонка

```
RecordSummary.exe --detect                 кто сейчас держит микрофон
RecordSummary.exe --record 10              записать 10 с
RecordSummary.exe --process "папка звонка"  транскрипт и саммари готовой записи
```
Результат — в `recordsummary.log`.
