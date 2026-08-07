# Zapret Helper

Готовый список хостов для обхода DPI-блокировок через [zapret](https://github.com/bol-van/zapret).

Выберите **любой** .exe, запустите его — помощник покажет все серверы,
к которым обращается приложение. Нажмите «Копировать» — чистый столбик
доменов для `list-general.txt` или любого другого list-файла zapret.

Работает с любыми приложениями: игры, лаунчеры, браузерные оболочки
(WebView2, Electron, CEF, Saucer), мессенджеры.

## Возможности

- **Любое приложение** — укажите .exe, всё остальное автоматически
- **Имена хостов из DNS** — системный кэш + журнал DNS-клиента → имена вместо IP
- **Дочерние процессы** — BFS по дереву процессов, даже если родитель закрылся
- **Автокатегоризация**:
  - `app` — серверы самого приложения (вверху, красный)
  - `windows` — системный шум (телеметрия, Edge, Google DNS) — серым внизу
- **Группировка** — одинаковые имена с разными IP → одна строка
- **Копирование столбиком** — только домены приложения, готово для zapret
- **Автонастройка DNS** — одно нажатие + UAC, больше ничего не нужно
- **Без прав администратора** (только UAC для DNS-лога)
- **Перетаскивание окна** за заголовок

## Интерфейс

```
┌─ Zapret Helper by plyushkin96 ────────────────────────────────┐
│                                                                │
│  Шаги                                                         │
│  1. Выберите .exe приложения                                  │
│  2. Запустите приложение                                      │
│  3. Справа появятся адреса                                    │
│  4. Нажмите Копировать                                        │
│                                                                │
│  [📁 Выбрать .exe приложения]                                  │
│  ● Следим за: my-app (PID 1234)  дети: 7                       │
│                                                                │
│                     │  Найденные адреса               [4]     │
│                     │  ┌──────────────────────────────────┐   │
│                     │  │ api.my-app.com    TCP app child   │   │
│                     │  │   104.26.14.175:443               │   │
│                     │  │ cdn.my-app.com    dns app         │   │
│                     │  │   172.67.136.168                  │   │
│                     │  │   104.21.62.143                   │   │
│                     │  │ loader.my-app.com dns app         │   │
│                     │  │   104.26.15.175                   │   │
│                     │  │ ──────────────────────────────── │   │
│                     │  │ dns.google    TCP windows child    │   │
│                     │  │ substrate.office.com TCP windows  │   │
│                     │  └──────────────────────────────────┘   │
│                     │  [⧉ Копировать]  [✕ Очистить]          │
└───────────────────────────────────────────────────────────────┘
```

## Установка

1. [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) — уже есть в Windows 10/11
2. Склонируйте репозиторий
3. Запустите `Zapret-Helper.cmd`

При первом запуске появится кнопка «Настроить DNS» — нажмите, подтвердите UAC,
и имена хостов будут определяться автоматически.

## Как работает

| Механизм | Что делает |
|---|---|
| TCP-таблица | `GetTcpRow` → все TCPv4/v6 подключения, фильтр по PID |
| DNS-кэш | `DnsGetCacheDataTable` (dnsapi.dll) — системный кэш DNS |
| DNS-журнал | `Microsoft-Windows-DNS-Client/Operational` — захват ВСЕХ DNS-запросов |
| PTR | Асинхронные обратные DNS-запросы для неопознанных IP |
| Дерево процессов | `NtQueryInformationProcess` → parent PID → BFS потомков |
| Категоризация | Имя приложения в домене = `app`, известный шум = `windows` |

## Сборка

```powershell
csc /target:winexe /out:Zapret-Helper.exe `
  /reference:"WPF\WindowsBase.dll" `
  /reference:"WPF\PresentationCore.dll" `
  /reference:"WPF\PresentationFramework.dll" `
  /reference:"System.Xaml.dll" `
  /reference:"Microsoft.Web.WebView2.Core.dll" `
  /reference:"Microsoft.Web.WebView2.Wpf.dll" `
  /reference:"System.Web.Extensions.dll" `
  /resource:"index.html","ZapretHelper.index.html" `
  AppMain.cs
```

Требуется .NET Framework 4.x и [WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2).

## Флаги

| Флаг | Назначение |
|---|---|
| `-Debug` | Писать `debug.log` |
| `-Test <exe> <sec>` | Автовыбор .exe и тест N секунд |
| `-SmokeTest` | Проверка WebView2 + JS-мост |
| `-AutoPick` | Автонажатие «Выбрать .exe» |
| `-ClickPick` | Эмуляция клика по кнопке |

## Лицензия

MIT
