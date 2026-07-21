# ZLauncher — сайт (GitHub Pages)

Одна страница: слева название, описание и **Скачать**, справа превью окна лаунчера.

## Настройка

В `js/main.js`:

```js
const DOWNLOAD_URL =
  "https://github.com/YOUR_USERNAME/ZLauncher/releases/latest";
```

## Публикация

```bash
cd zlauncher-site
git init
git add .
git commit -m "ZLauncher site"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/zlauncher-site.git
git push -u origin main
```

**Settings → Pages → Deploy from branch → main / root**
