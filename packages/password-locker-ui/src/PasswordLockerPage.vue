<script setup>
// 這個元件是 FileLocker.App 跟 PasswordVault.exe 共用的密碼庫整體畫面（見 FileLocker repo
// docs/adr/0004-shared-password-locker-ui-npm-package.md，含依賴注入介面清單那一節）。
//
// 目前狀態：對外介面（props／訊息路由／defineExpose）跟「停用 Passkey／停用恢復金鑰」的
// 簡化版密碼提示彈窗已經接好，但主畫面（帳密清單／新增編輯表單／TOTP／設定頁子區塊）還是
// 占位文字，還沒從 FileLocker.Web 的 App.vue 搬過來，那是下一輪工作。
import { ref, nextTick } from 'vue'
import { t } from './i18n.js'

const props = defineProps({
  lang: {
    type: String,
    default: 'zh-TW'
  },
  // 淺色／深色主題——Passkey／恢復金鑰圖示等要跟著換黑白版本用得到（見 ADR-0004）。
  theme: {
    type: String,
    default: 'light'
  },
  sendMessage: {
    type: Function,
    required: true
  },
  requestMessage: {
    type: Function,
    required: true
  },
  // 以下四個是 host 本來就有一套現成視覺實作的橫向基礎設施，整套當 props 注入，
  // 確保套件跟 host 其他畫面視覺一致（見 ADR-0004「依賴注入介面的完整清單」）。
  showToast: {
    type: Function,
    required: true
  },
  askConfirm: {
    type: Function,
    required: true
  },
  translateError: {
    type: Function,
    required: true
  },
  // 選填：只有「已加密檔案」類別的憑證要關聯到一個 Vault 項目時才用得到。不提供就隱藏
  // 這部分 UI，PasswordVault 獨立版預設不傳這兩個 prop（見 PasswordVault_獨立化_規劃.md
  // 第 6 節），不需要假造一份空陣列。
  vaultItems: {
    type: Array,
    default: null
  },
  refreshList: {
    type: Function,
    default: null
  }
})

// ---- 訊息路由：套件內部自己管一份 pendingResolvers，host 只需要把收到的每一則訊息都轉發
// 給 handleMessage 這一個統一入口，不需要知道密碼庫有哪些訊息類型（見 ADR-0004）。原本
// FileLocker.Web 那 25 條 messageHandlers 條目全是機械化的 resolvePending(同名, data)，
// 抽成套件之後這層细節不該再讓 host 知道。
const pendingResolvers = {}

function requestPasswordLockerMessage(requestType, responseType, payload = {}) {
  return new Promise((resolve) => {
    pendingResolvers[responseType] = resolve
    props.sendMessage(requestType, payload)
  })
}

/// host 收到任何 WebView2 訊息時，只要 type 看起来是密碼庫相關的，就整則轉發給這個方法——
/// 找不到對應的等待中請求會安靜跳過（跟 FileLocker.Web 既有 useIpc.js 的 resolvePending
/// 同一個既有假設：同一種回應類型同時間只會有一個等待中的請求）。
function handleMessage(type, data) {
  pendingResolvers[type]?.(data)
  delete pendingResolvers[type]
}

defineExpose({
  handleMessage,
  // 加密成功後「要不要順便存進密碼庫」的鉤子——host（加密頁）在加密完成後透過 template ref
  // 呼叫這個方法（見 ADR-0004）。真正的邏輯（查詢部件狀態、跳確認彈窗、驗證、實際儲存）還沒
  // 從 App.vue 的 maybeOfferSaveEncryptedFilesToLocker 搬過來，這裡先留介面签章。
  offerSaveEncryptedFiles(password, items) {
    // TODO：下一輪從 App.vue 搬移實際邏輯進來。
    void password
    void items
  }
})

// ---- 密碼庫自己的簡化版密碼提示彈窗（停用 Passkey／停用恢復金鑰用，見 ADR-0004）——
// 不再共用 host 的 passwordPromptContext，因為那個彈窗還服務雙擊 .locked 檔案、資料夾防護
// 解鎖等密碼庫以外的情境；這裡是密碼庫自己專屬、只認得這兩種模式的獨立版本。
const simplifiedPasswordPromptState = ref(null) // { mode, resolve } | null
const simplifiedPasswordPromptValue = ref('')
const showSimplifiedPasswordPromptValue = ref(false)
const simplifiedPasswordPromptInputRef = ref(null)

/// 回傳 Promise<string|null>——使用者送出密碼 resolve 密碼字串，取消／按 Esc resolve null。
/// mode 目前只有兩種：'disablePasskey'／'disableRecoveryKey'，對應各自的提示文案。
function openSimplifiedPasswordPrompt(mode) {
  return new Promise((resolve) => {
    simplifiedPasswordPromptValue.value = ''
    showSimplifiedPasswordPromptValue.value = false
    simplifiedPasswordPromptState.value = { mode, resolve }
    nextTick(() => simplifiedPasswordPromptInputRef.value?.focus())
  })
}

function submitSimplifiedPasswordPrompt() {
  if (!simplifiedPasswordPromptValue.value) {
    return
  }
  simplifiedPasswordPromptState.value?.resolve(simplifiedPasswordPromptValue.value)
  simplifiedPasswordPromptState.value = null
}

function cancelSimplifiedPasswordPrompt() {
  simplifiedPasswordPromptState.value?.resolve(null)
  simplifiedPasswordPromptState.value = null
}

function handleSimplifiedPasswordPromptKeydown(event) {
  if (event.key === 'Escape' && simplifiedPasswordPromptState.value) {
    cancelSimplifiedPasswordPrompt()
  }
}
</script>

<template>
  <div class="password-locker-page" @keydown="handleSimplifiedPasswordPromptKeydown">
    <h1 class="password-locker-page__title">{{ t('pageTitle', props.lang) }}</h1>
    <p class="password-locker-page__body">{{ t('pageDescription', props.lang) }}</p>

    <Transition name="password-locker-modal">
      <div v-if="simplifiedPasswordPromptState" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('verifyTitle', props.lang) }}</h2>
          <p class="modal__subtitle">
            {{ t(simplifiedPasswordPromptState.mode === 'disablePasskey'
              ? 'disablePasskeyPasswordPrompt'
              : 'disableRecoveryKeyPasswordPrompt', props.lang) }}
          </p>
          <div class="password-field">
            <input
              ref="simplifiedPasswordPromptInputRef"
              v-model="simplifiedPasswordPromptValue"
              :type="showSimplifiedPasswordPromptValue ? 'text' : 'password'"
              class="text-input"
              @keyup.enter="submitSimplifiedPasswordPrompt"
            />
            <button
              type="button"
              class="password-field__toggle"
              :aria-label="t(showSimplifiedPasswordPromptValue ? 'hide' : 'show', props.lang)"
              @click="showSimplifiedPasswordPromptValue = !showSimplifiedPasswordPromptValue"
            >
              <svg v-if="showSimplifiedPasswordPromptValue" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </button>
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" type="button" @click="cancelSimplifiedPasswordPrompt">{{ t('cancel', props.lang) }}</button>
            <button
              class="button button--primary"
              type="button"
              :disabled="!simplifiedPasswordPromptValue"
              @click="submitSimplifiedPasswordPrompt"
            >
              {{ t('verifyTitle', props.lang) }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
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

.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(20, 22, 28, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.modal {
  background: var(--color-surface, #fff);
  border-radius: 16px;
  padding: 1.75rem;
  width: min(360px, 90vw);
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.25);
}

.modal__title {
  font-size: 1.15rem;
  font-weight: 700;
  margin: 0 0 0.5rem;
  color: var(--color-text-primary, #1a1a1a);
}

.modal__subtitle {
  font-size: 0.9rem;
  color: var(--color-text-secondary, #666);
  margin: 0 0 1rem;
  line-height: 1.5;
}

.modal__footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1.25rem;
}

.password-field {
  position: relative;
  display: flex;
  align-items: center;
}

.text-input {
  flex: 1;
  padding: 0.55rem 2.5rem 0.55rem 0.75rem;
  border-radius: 8px;
  border: 1px solid var(--color-border, #ccc);
  font-size: 0.95rem;
}

.password-field__toggle {
  position: absolute;
  right: 0.5rem;
  width: 22px;
  height: 22px;
  border: none;
  background: none;
  color: var(--color-text-secondary, #666);
  cursor: pointer;
  padding: 0;
}

.button {
  padding: 0.5rem 1.1rem;
  border-radius: 8px;
  border: none;
  font-size: 0.9rem;
  cursor: pointer;
}

.button--primary {
  background: var(--color-accent, #a37e2c);
  color: #fff;
}

.button--primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.button--secondary {
  background: var(--color-surface-secondary, #eee);
  color: var(--color-text-primary, #1a1a1a);
}

.password-locker-modal-enter-active,
.password-locker-modal-leave-active {
  transition: opacity 160ms ease;
}

.password-locker-modal-enter-from,
.password-locker-modal-leave-to {
  opacity: 0;
}
</style>
