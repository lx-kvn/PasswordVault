<script setup>
// 這個元件是 FileLocker.App 跟 PasswordVault.exe 共用的密碼庫整體畫面（見 FileLocker repo
// docs/adr/0004-shared-password-locker-ui-npm-package.md）——目前只是骨架，真正的帳密清單／
// 新增編輯表單／TOTP 等畫面還沒從 FileLocker.Web 的 App.vue 搬過來，那是下一輪的工作。
//
// 三個 props 是這個元件對外的完整介面契約：
// - lang：決定顯示語言，元件內部自己有完整的 zh-TW/en 翻譯表（不透過 props 傳翻譯字串）。
// - sendMessage／requestMessage：呼叫端注入的 IPC 函式，元件不假設宿主一定是 WebView2
//   （見 ADR-0004）——FileLocker.Web／PasswordVault.Web 各自把自己的
//   window.chrome.webview.postMessage 包成同樣形狀的函式傳進來。
import { t } from './i18n.js'

const props = defineProps({
  lang: {
    type: String,
    default: 'zh-TW'
  },
  sendMessage: {
    type: Function,
    required: true
  },
  requestMessage: {
    type: Function,
    required: true
  }
})
</script>

<template>
  <div class="password-locker-page">
    <h1 class="password-locker-page__title">{{ t('placeholderTitle', props.lang) }}</h1>
    <p class="password-locker-page__body">{{ t('placeholderBody', props.lang) }}</p>
  </div>
</template>

<style scoped>
/* 套件自帶預設 CSS 變數（帶 fallback 值），外層可以覆蓋——見 ADR-0004。
   FileLocker.Web 現有的 .theme-vault 等機制會從外層覆蓋這些變數；PasswordVault.Web
   （全新專案，沒有 FileLocker 那套現成主題 CSS）不提供也能看到合理的預設樣式。 */
.password-locker-page {
  padding: 2rem;
  color: var(--color-text-primary, #1a1a1a);
  font-family: var(--font-family-base, system-ui, sans-serif);
}

.password-locker-page__title {
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--color-accent, #a37e2c);
  margin: 0 0 0.75rem;
}

.password-locker-page__body {
  font-size: 0.95rem;
  color: var(--color-text-secondary, #666);
  line-height: 1.6;
  margin: 0;
}
</style>
