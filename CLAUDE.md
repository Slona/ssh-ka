# Сервер cafe.plushka.tv (msk-1-vm-fwwk)

Единый доступ для всех проектов: `ssh cafe-claude` (пользователь `claude`, ключ `~/.ssh/cafe_claude_ed25519`,
алиас в `~/.ssh/config`, sudo без пароля). Права root — через `sudo`. Пароль не вводить.
Старые ключи (`cafe_twitch_subs_deploy_ed25519`, `slochat_deploy_ed25519`) остаются на сервере, но для новых задач не используются.
Общая карта сервера: `/var/www/<проект>` — статика, `/etc/nginx/sites-enabled/<домен>` — nginx, pm2 — под root и tim.

## Этот проект на сервере

- Сайт: `/var/www/sshka` (владелец root), nginx — `/etc/nginx/sites-enabled/sshka.slona.me` (домен sshka.slona.me).
- Выкладка: собрать локально и залить через `ssh cafe-claude` (запись в `/var/www/sshka` — через `sudo`),
  затем `sudo nginx -t && sudo systemctl reload nginx` при правке конфига.
