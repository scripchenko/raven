# Подключение Gmail в raven

Gmail в raven работает через Gmail API. Вход Google открывается в системном браузере и возвращается в приложение через локальный loopback-адрес; пароль Google в raven не вводится.

В v0.1.0 Google Desktop OAuth client configuration **не включена** в installer. Для подключения Gmail пользователь должен предоставить собственный клиент:

1. Настроить проект Google Cloud с включённым Gmail API и OAuth consent screen.
2. Создать OAuth client типа **Desktop application** и скачать его JSON-конфигурацию.
3. Сохранить JSON как `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. Файл должен содержать объект `installed` с `client_id` и `client_secret`. Не добавляйте настоящий файл в Git или общедоступные отчёты.
4. В raven выбрать добавление почтового аккаунта Gmail и завершить вход/согласие в системном браузере.

При новом подключении текущий код запрашивает Gmail scope `gmail.modify` для поддерживаемых операций с письмами и черновиками. Существующему аккаунту с доступом только для чтения может потребоваться повторное согласие. Не предоставляйте доступ, если не согласны с отображаемыми Google разрешениями.

Refresh credential хранится локально под защитой Windows DPAPI `CurrentUser`; access token используется во время работы приложения. Локальный JSON OAuth-клиента отдельно не шифруется raven и остаётся необходимым для последующей авторизации. Подробнее о техническом потоке — в [gmail-oauth-development.md](gmail-oauth-development.md).
