# Gmail OAuth: конфигурация для разработки

raven использует Google OAuth 2.0 authorization-code flow для Desktop application. Вход открывается в системном браузере, ответ принимается одноразовым локальным listener на `http://127.0.0.1:<random-port>/oauth2/callback/`.

Конфигурация клиента читается из `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. Это локальный Google JSON с объектом `installed`, полями `client_id` и `client_secret`. Его, коды авторизации и токены нельзя коммитить. Исторический `credentials.example.json` в Git history был только placeholder; он не подходит для реального входа.

Новое подключение Gmail в текущем коде запрашивает `https://www.googleapis.com/auth/gmail.modify`. Для старых credential с `gmail.readonly` предусмотрен запрос расширенного доступа, когда операция этого требует. Scope `gmail.send` отдельно не запрашивается текущим потоком. Фактический запрос и согласие всегда нужно сверять с текущим кодом и экраном Google перед выпуском новой версии.

Refresh credential и метаданные, нужные для его обновления, хранятся как типизированная запись под защитой Windows DPAPI `CurrentUser`. Access token остаётся в памяти. Конфигурация Desktop OAuth client не входит в installer и не шифруется приложением.

Пользовательские шаги описаны в [gmail-setup.md](gmail-setup.md).
