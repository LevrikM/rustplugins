# 📦  

> Плагины: **AdminMenu** · **DiscordReports** · **WelcomeMessage**  
> Требования: Rust Dedicated Server + [uMod / Oxide](https://umod.org/games/rust)

---

```
oxide/plugins/AdminMenu.cs
oxide/plugins/DiscordReports.cs
oxide/plugins/WelcomeMessage.cs
```

```
o.load AdminMenu
o.load DiscordReports
o.load WelcomeMessage
```

---
 в папке `oxide/data/Rustalgia/` положи туда:

| Файл | Назначение |
|------|-----------|
| `logo.png` или `logo.jpg` | Логотип сервера (показывается в шапке AdminMenu) |
| `banner.png` или `banner.jpg` | Баннер (показывается на вкладке Dashboard) |

> **Рекомендуемые размеры:**  
> Логотип — квадрат, минимум `128×128 px`  
> Баннер — широкоформатный, например `800×200 px`

Если файлов нет — плагин загрузится без ошибок, просто без картинок.

---

## Настроить конфиги

Конфиги создаются автоматически при первом запуске в папке `oxide/config/`.

---

### 🔧 `oxide/config/AdminMenu.json`

```json
{
  "Server Display Name": "МОЙ СЕРВЕР",
  "Server Subtitle / Slogan": "SERVER CONTROL CENTER",
  "Broadcast Prefix": "<color=#A855F7>[МОЙ СЕРВЕР]</color>",
  "Master Admin SteamIDs (Full access to all tabs & config editor)": [
    "ВАШ_STEAMID64"
  ],
  "Moderator SteamIDs (Access to players, reports, self-tools)": [
    "STEAMID64_МОДЕРАТОРА_1",
    "STEAMID64_МОДЕРАТОРА_2"
  ],
  "Custom Banner URL (Optional override)": "",
  "Custom Logo URL (Optional override)": "",
  "Admin Action Alert Sound": "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab"
}
```


### 🔧 `oxide/config/DiscordReports.json`

```json
{
  "Discord Webhook URL": "https://discord.com/api/webhooks/XXXXXXXX/XXXXXXXXXXXXXXXX",
  "Discord Mention Role (e.g. @here or <@&ROLE_ID>, or empty)": "@here",
  "Discord Embed Color (Decimal code, 16724787 = Red)": 16724787,
  "Report Cooldown for Regular Players (Seconds)": 60,
  "Notify Online Admins In-Game (true/false)": true,
  "Server Name for Discord": "МОЙ СЕРВЕР",
  "Admin Alert Sound Prefab": "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab",
  "Discord Invite URL for Checks": "https://discord.gg/ВАШ_ДИСКОРД",
  "Discord Voice Channel Name for Checks": "#проверка",
  "Check Duration in Seconds (Default: 300 = 5 min)": 300
}
```

---
###  `oxide/config/WelcomeMessage.json`

```json
{
  "ServerName": "МОЙ СЕРВЕР",
  "DiscordUrl": "discord.gg/ВАШ_ДИСКОРД",
  "DelaySeconds": 3.0
}
```

---

## структура файлов

```
server/
└── oxide/
    ├── plugins/
    │   ├── AdminMenu.cs          ← главный плагин
    │   ├── DiscordReports.cs     ← репорты + проверки
    │   └── WelcomeMessage.cs     ← велком в чат
    ├── config/
    │   ├── AdminMenu.json        ← настраивается вручную
    │   ├── DiscordReports.json   ← настраивается вручную
    │   └── WelcomeMessage.json   ← настраивается вручную
    └── data/
        └── Rustalgia/
            ├── logo.png          ← логотип сервера
            └── banner.png        ← баннер (опционально)
```
