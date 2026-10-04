# SeminarDesk

Локальный учебный HTTP API для обращений о технических проблемах на семинарах.

## Документы

- [Паспорт проекта](PROJECT.md) — назначение, роли, сценарии и технологии.
- [Модель угроз](docs/THREAT_MODEL.md) — границы доверия и угрозы.
- [Требования безопасности](docs/SECURITY_REQUIREMENTS.md) — SR-01–SR-10.
- [Проектные решения](docs/DESIGN_DECISIONS.md) — обоснование механизмов защиты.
- [Вклад участников](CONTRIBUTIONS.md) и [использование ИИ](AI_USAGE.md).

## Запуск

Нужен .NET SDK 8.0.401 или совместимый SDK .NET 8.

```bash
dotnet run --project src/Helpdesk.Api --launch-profile local
```

API запустится на `http://127.0.0.1:5080`. Проверить запуск можно запросом
`GET http://127.0.0.1:5080/health`. При первом запуске в Development создаются
учебные аккаунты и локальный файл хранилища.

**Тестовые данные:**

| Логин | Пароль | Роль |
| --- | --- | --- |
| `alice` | `Alice-demo-2026!` | user |
| `bob` | `Bob-demo-2026!` | user |
| `operator` | `Operator-demo-2026!` | operator |

## Swagger

После запуска API Swagger доступен по адресу `http://127.0.0.1:5080/swagger`
в окружении Development.

## Postman

1. Импортируйте [SeminarDesk.postman_collection.json](postman/SeminarDesk.postman_collection.json) в Postman.
2. Убедитесь, что переменная коллекции `baseUrl` равна `http://127.0.0.1:5080`.
3. Запустите коллекцию целиком через Collection Runner или отправляйте запросы по порядку.
