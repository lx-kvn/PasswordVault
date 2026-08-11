<script setup>
// PasswordVault.exe 的前端進入點——跟 FileLocker.Web 的 App.vue 不同，這裡刻意保持極簡：
// 密碼庫是這支程式唯一的功能，不需要分頁切換／其他功能的殼，直接掛共用套件的
// <PasswordLockerPage>（見 FileLocker repo docs/adr/0004-shared-password-locker-ui-npm-package.md，
// 含依賴注入介面清單那一節）。
import { ref } from 'vue'
import { PasswordLockerPage } from '@lx-kvn/password-locker-ui'
import '@lx-kvn/password-locker-ui/style.css'
import { sendMessage, requestMessage } from './composables/useIpc.js'

// TODO：語言／主題設定要接到 PasswordVault.exe 的設定檔（見 PasswordVault_獨立化_規劃.md
// 第 15 節「設定不共享」，這輪還沒有設定頁 UI），先固定 zh-TW／light。
const lang = 'zh-TW'
const theme = 'light'

// ---- toast/confirm/translateError：PasswordVault.Web 自己還沒有正式的視覺實作（FileLocker.Web
// 那套是配合它既有畫面風格做的），這裡先給一份能動、非原生對話框的最小版本——理由跟
// FileLocker.Web 拒絕原生 alert()/confirm() 一致：原生對話框在桌面應用程式裡會露出瀏覽器痕跡。
// 之後 PasswordVault.Web 有自己的視覺風格定案後再回頭做成正式版本。
const toasts = ref([])
function showToast(message, kind = 'error') {
  const id = `${Date.now()}-${Math.random()}`
  toasts.value.push({ id, message, kind })
  setTimeout(() => {
    toasts.value = toasts.value.filter((toast) => toast.id !== id)
  }, 6000)
}

const confirmState = ref(null) // { message, confirmLabel, cancelLabel, resolve }
function askConfirm(message, options = {}) {
  return new Promise((resolve) => {
    confirmState.value = {
      message,
      confirmLabel: options.confirmLabel || '確定',
      cancelLabel: options.cancelLabel || '取消',
      resolve
    }
  })
}
function resolveConfirm(result) {
  confirmState.value?.resolve(result)
  confirmState.value = null
}

// 還沒有翻譯表，先直接退回 fallback 訊息——跟 FileLocker.Web 的 translateError 找不到對應
// 翻譯時的退回行為一致。
function translateError(errorCode, errorDetail, fallbackMessage) {
  return fallbackMessage
}
</script>

<template>
  <PasswordLockerPage
    :lang="lang"
    :theme="theme"
    :send-message="sendMessage"
    :request-message="requestMessage"
    :show-toast="showToast"
    :ask-confirm="askConfirm"
    :translate-error="translateError"
  />

  <div class="toast-stack">
    <div v-for="toast in toasts" :key="toast.id" class="toast" :class="`toast--${toast.kind}`">
      {{ toast.message }}
    </div>
  </div>

  <div v-if="confirmState" class="confirm-overlay">
    <div class="confirm-box">
      <p>{{ confirmState.message }}</p>
      <div class="confirm-actions">
        <button type="button" @click="resolveConfirm(false)">{{ confirmState.cancelLabel }}</button>
        <button type="button" @click="resolveConfirm(true)">{{ confirmState.confirmLabel }}</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.toast-stack {
  position: fixed;
  right: 1rem;
  bottom: 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  z-index: 1000;
}

.toast {
  padding: 0.6rem 1rem;
  border-radius: 8px;
  background: #333;
  color: #fff;
  font-size: 0.85rem;
}

.toast--success {
  background: #2f7d46;
}

.confirm-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1001;
}

.confirm-box {
  background: #fff;
  border-radius: 12px;
  padding: 1.5rem;
  max-width: 360px;
}

.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1rem;
}
</style>
