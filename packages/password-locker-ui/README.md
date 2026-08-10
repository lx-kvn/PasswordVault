# @lx-kvn/password-locker-ui

`FileLocker.App`（透過 `FileLocker.Web`）與 `PasswordVault.exe`（透過 `PasswordVault.Web`）共用的密碼庫畫面元件。決策背景見 FileLocker repo 的 [ADR-0004](https://github.com/lx-kvn/FileLocker/blob/main/docs/adr/0004-shared-password-locker-ui-npm-package.md)。

**目前狀態：骨架階段。** 匯出的 `<PasswordLockerPage>` 只是一個占位元件，真正的帳密清單／新增編輯表單／TOTP 等畫面還沒從 `FileLocker.Web` 的 `App.vue` 搬過來，那是下一輪的工作。這一輪先確保：npm workspace／套件建置流程／對外 props 介面本身是通的。

## 用法

```vue
<script setup>
import { PasswordLockerPage } from '@lx-kvn/password-locker-ui'
import '@lx-kvn/password-locker-ui/style.css'
import { sendMessage, requestMessage } from './composables/useIpc.js'
</script>

<template>
  <PasswordLockerPage lang="zh-TW" :send-message="sendMessage" :request-message="requestMessage" />
</template>
```

## Props 介面契約

| Prop | 型別 | 說明 |
| --- | --- | --- |
| `lang` | `String` | `'zh-TW'` 或 `'en'`，決定顯示語言。元件內部自己帶完整翻譯表，不透過 props 傳遞翻譯字串。 |
| `sendMessage` | `Function` | 送出一則不需要回應的訊息，簽章比照現有 `useIpc.js` 的 `sendMessage(type, payload)`。 |
| `requestMessage` | `Function` | 送出一則訊息並等待對應的回應，簽章比照現有 `useIpc.js` 的 `requestMessage(requestType, responseType, payload)`，回傳 `Promise`。 |

元件不假設宿主一定是 WebView2——`sendMessage`／`requestMessage` 由呼叫端注入，實際上兩邊宿主現在都是包一層 `window.chrome.webview.postMessage`，但元件介面本身不寫死這個假設。

## 樣式

元件內部用 `var(--color-accent, #a37e2c)` 這種帶 fallback 值的寫法引用 CSS 自訂屬性，外層可以覆蓋（`FileLocker.Web` 現有的 `.theme-vault` 等主題機制會從外層覆蓋）。發布出去的 `dist/password-locker-ui.css` 要跟著 JS 一起引入。

## 開發

這個套件是 PasswordVault repo 的 npm workspace 成員之一（見 repo 根目錄的 `package.json`）：

```
npm install       # 在 repo 根目錄跑，一次裝好所有 workspace 成員
npm run build -w @lx-kvn/password-locker-ui
```

`src/PasswordVault.Web/` 透過 workspace 連結本地版本，改完這裡的程式碼、重新 `build` 一次，`PasswordVault.Web` 馬上就能看到最新版本，不需要先 `npm publish`。`FileLocker.Web`（外部 repo）則是透過正常的 npm 安裝流程取得已發布版本，且金定精確版本號（見 ADR-0004）。
