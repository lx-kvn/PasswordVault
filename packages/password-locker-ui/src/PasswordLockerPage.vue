<script setup>
// 這個元件是 FileLocker.App 跟 PasswordVault.exe 共用的密碼庫整體畫面（見 FileLocker repo
// docs/adr/0004-shared-password-locker-ui-npm-package.md，含依賴注入介面清單那一節）。
//
// 主畫面（帳密清單／新增編輯表單／TOTP／帳密管理子區塊）已經從 FileLocker.Web 的 App.vue
// 搬過來了。App.vue 原本是「密碼庫」跟「設定」兩個分頁分開放（設定頁裡才有改密碼／Passkey／
// 恢復金鑰／CSV／解除安裝部件這些管理選項），這裡是獨立套件、沒有 App.vue 那套多分頁殼層可以
// 借放，所以管理選項併到同一頁最下面，不是另外開一個分頁。
import { ref, computed, watch, nextTick, onMounted, onUnmounted } from 'vue'
import jsQR from 'jsqr'
import { t } from './i18n.js'
import {
  computeTotpCode,
  parseTotpInput,
  isTotpInputComplete,
  totpRingOffset,
  totpSecondsRemaining,
  TOTP_RING_CIRCUMFERENCE
} from './totp.js'

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
// 抽成套件之後這層细節不該再讓 host 知道——下面所有 request/response 呼叫都改走這個 helper，
// 不需要另外註冊逐一對應的處理常式。
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
  // 呼叫這個方法（見 ADR-0004）。邏輯搬自 App.vue 的 maybeOfferSaveEncryptedFilesToLocker。
  offerSaveEncryptedFiles: offerSaveEncryptedFilesToLocker
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

// ---- 密碼庫狀態：搬自 FileLocker.Web App.vue（見檔案開頭註解），欄位/邏輯逐一對應原本
// 的 passwordLocker* 系列 ref／函式，只調整呼叫外部依賴的地方（sendMessage／showToast／
// askConfirm／translateError／t() 改吃 props 或這個套件自己的 t()，原本 'passwordLocker.'
// 開頭的 key 前綴在套件的語言檔裡已經去掉，見 locales/*.json）。
const passwordLockerModuleStatus = ref('unknown') // 'unknown' | 'notInstalled' | 'broken' | 'ok'
const passwordLockerConfigured = ref(false)
const isInstallingPasswordLockerModule = ref(false)
const passwordLockerItems = ref([])
const isLoadingPasswordLocker = ref(false)
const passwordLockerSetupPassword = ref('')
const passwordLockerSetupPasswordConfirm = ref('')
const showPasswordLockerSetupPassword = ref(false)
const passwordLockerPasskeyEnabled = ref(false)
const passwordLockerRecoveryKeyEnabled = ref(false)
const passwordLockerSessionTimeoutMinutes = ref(1)
const passwordLockerSessionExpiresAt = ref(0)
const passwordLockerSearchQuery = ref('')
// 備註是加密欄位，前端沒辦法直接比對——已驗證時（有 app session）才問後端解密比對，這裡存
// 上一次查詢比對到的 id，跟明文欄位的比對結果在 computed 裡合併。沒驗證過就一直是空集合，
// 搜尋只退回比對明文欄位，不會整個壞掉或跳錯誤。
const passwordLockerNotesMatchIds = ref(new Set())
let passwordLockerSearchDebounceTimer = null
const passwordLockerWebsiteSort = ref('alphabetical') // 'alphabetical' | 'time'
const passwordLockerFileSort = ref('time') // 'alphabetical' | 'time'
const passwordLockerViewFilter = ref('all') // 'all' | 'website' | 'file'
// 密碼庫內部小分頁：帳密清單／設定（改密碼/Passkey/恢復金鑰/CSV）——設定原本併在清單最
// 下面，改成獨立按鈕切換到這裡，不要一直黏在清單下面。
const activeVaultSubTab = ref('list') // 'list' | 'settings'
// id -> 明文密碼，只存在這個元件的記憶體裡，不落地；跟後端 session 一樣沒有做「切分頁就清除」。
const passwordLockerRevealedPasswords = ref({})
// 哪幾筆目前是「明文顯示」狀態——跟 passwordLockerRevealedPasswords 分開：後者是「有沒有解密過」，
// 這個才是「現在是不是遮住」，切換顯示/隱藏不用重新驗證或重新解密，純粹前端狀態。
const passwordLockerVisibleIds = ref(new Set())
// 帳號欄位的顯示/隱藏是獨立於密碼那顆眼睛圖示的另一組互動（點帳號文字本身），
// 形狀比照上面兩個 ref，只是分開管理，兩邊誰顯示誰隱藏互不影響。
const passwordLockerRevealedUsernames = ref({})
const passwordLockerUsernameVisibleIds = ref(new Set())
const passwordLockerSelectedIds = ref(new Set())
const passwordLockerRecoveryKeyDisplay = ref('') // 非空字串時顯示恢復金鑰彈窗
const passwordLockerRecoveryKeySaveState = ref('') // '' | 'saved'

// 驗證彈窗：多了「改用恢復金鑰」的切換。
// pendingAction 是驗證通過後要接著做的事：{ type: 'reveal'|'copy'|'delete'|'save', ... }
const passwordLockerVerifyState = ref(null) // { usingRecoveryKey, pendingAction }
const passwordLockerVerifyValue = ref('')
const showPasswordLockerVerifyValue = ref(false)
// 擋連續點擊：Passkey 驗證期間（Windows Hello 對話框開著）或驗證彈窗已經開著時，
// 再點一次「設定 Passkey」之類的按鈕不該疊出第二個 Windows Hello 提示——沒有這道防線，
// 連點會讓每次點擊各自觸發一次獨立的 Passkey 嘗試，越點越多個提示疊在一起。
const isPasswordLockerAuthBusy = ref(false)

// 新增/編輯表單
const passwordLockerFormState = ref(null) // { id, category, title, domains, domainInput, username, password, notes }
const showPasswordLockerFormPassword = ref(false)

// 表單裡的 TOTP 區塊：totpDraft 是「這次存檔要不要動 TOTP、動成什麼」的暫存——null 代表
// 這次存檔完全不碰 TOTP（既有紀錄的設定維持原樣），{secret:'', ...} 空字串代表使用者按了
// 「移除」，非空字串是設定新密鑰。跟 passwordLockerFormState 分開存放，因為表單開啟當下
// 不會預先解密既有的 TOTP 密鑰——existingHasTotp 只記「有沒有」，不記內容。
const passwordLockerTotpDraft = ref(null) // null | { secret, algorithm, digits, period }
const passwordLockerTotpExistingHasTotp = ref(false)
const passwordLockerTotpQrError = ref('')
const passwordLockerTotpPreviewCode = ref('')
let passwordLockerTotpPreviewTimer = null
// 純粹讓圓形倒數（totpRingOffset）在模板裡每秒重新算一次的觸發器——Vue 沒辦法自動偵測
// 「時間流逝」本身是個依賴，用一個每秒遞增的 ref 逼模板重新求值。
const passwordLockerTotpNowTick = ref(Date.now())

// 清單頁「顯示 TOTP」：跟密碼/帳號的顯示/隱藏是同一種互動模式（passwordLockerVisibleIds），
// 但額外存一份 { secret, algorithm, digits, period } 而不是單純字串，因為要在前端本地持續
// 算出輪替的碼。收合時整個刪掉這個 entry（見 hidePasswordLockerTotp），不留在記憶體裡。
const passwordLockerRevealedTotps = ref({}) // id -> { secret, algorithm, digits, period, code }
let passwordLockerTotpRefreshTimer = null

// 「關聯到現有帳號」
const passwordLockerPickerVisible = ref(false)
const passwordLockerAssociateState = ref(null) // null | { item, domainInput, titleInput }

// 重設密碼庫密碼
const passwordLockerChangePasswordState = ref(null) // { newPassword, confirm }
const showPasswordLockerChangePassword = ref(false)

// ---- 部件狀態／清單 ----

async function refreshPasswordLockerModuleStatus() {
  const data = await requestPasswordLockerMessage('getPasswordLockerModuleStatus', 'passwordLockerModuleStatusResult')
  passwordLockerModuleStatus.value = data.status
  return data.status
}

async function refreshPasswordLockerList() {
  const status = await refreshPasswordLockerModuleStatus()
  if (status !== 'ok') {
    return
  }
  isLoadingPasswordLocker.value = true
  const data = await requestPasswordLockerMessage('listPasswordLocker', 'passwordLockerListResult')
  isLoadingPasswordLocker.value = false
  passwordLockerConfigured.value = data.configured
  passwordLockerPasskeyEnabled.value = data.passkeyEnabled
  passwordLockerRecoveryKeyEnabled.value = data.recoveryKeyEnabled
  passwordLockerSessionTimeoutMinutes.value = data.sessionTimeoutMinutes
  passwordLockerItems.value = data.items
}

// 自動查 FileLocker 本體 GitHub Release 的資產列表，找相容的 PasswordLocker zip、下載、
// 解壓到暫存資料夾，成功後請使用者重啟讓它生效。找不到相容版本或查詢/下載失敗時退回開發布頁面
// 讓使用者自己確認狀況，不是把使用者晾在原地。
async function installPasswordLockerModuleAction() {
  if (isInstallingPasswordLockerModule.value) {
    return
  }
  isInstallingPasswordLockerModule.value = true
  try {
    const checkResult = await requestPasswordLockerMessage('checkForPasswordLockerModuleUpdate', 'checkForPasswordLockerModuleUpdateResult', {})
    if (!checkResult.success || !checkResult.available) {
      props.showToast(t(checkResult.success ? 'moduleInstallNotFound' : 'moduleInstallCheckFailed', props.lang))
      props.sendMessage('openReleasesPage')
      return
    }

    const installResult = await requestPasswordLockerMessage('installPasswordLockerModuleUpdate', 'installPasswordLockerModuleUpdateResult', {})
    if (!installResult.success) {
      props.showToast(t('moduleInstallFailed', props.lang))
      props.sendMessage('openReleasesPage')
      return
    }

    const confirmed = await props.askConfirm(t('moduleInstallRestartPrompt', props.lang), { confirmLabel: t('moduleInstallRestartConfirm', props.lang) })
    if (confirmed) {
      props.sendMessage('restartApp')
    }
  } finally {
    isInstallingPasswordLockerModule.value = false
  }
}

// 解除安裝部件：資料（憑證）不受影響，只移除 App 內的部件本身，跟更新/安裝一樣要重啟才真正
// 生效——這裡只是先寫標記，這個 session 裡部件繼續照常可用。
async function uninstallPasswordLockerModuleAction() {
  const confirmed = await props.askConfirm(t('uninstallModuleWarning', props.lang), {
    confirmLabel: t('uninstallModuleConfirm', props.lang),
    variant: 'danger'
  })
  if (!confirmed) {
    return
  }

  const result = await requestPasswordLockerMessage('uninstallPasswordLockerModule', 'uninstallPasswordLockerModuleResult', {})
  if (!result.success) {
    props.showToast(t('uninstallModuleFailed', props.lang))
    return
  }

  const restartConfirmed = await props.askConfirm(t('moduleInstallRestartPrompt', props.lang), { confirmLabel: t('moduleInstallRestartConfirm', props.lang) })
  if (restartConfirmed) {
    props.sendMessage('restartApp')
  }
}

async function submitPasswordLockerSetup() {
  if (!passwordLockerSetupPassword.value) {
    props.showToast(t('passwordRequired', props.lang))
    return
  }
  if (passwordLockerSetupPassword.value !== passwordLockerSetupPasswordConfirm.value) {
    props.showToast(t('passwordMismatch', props.lang))
    return
  }

  await requestPasswordLockerMessage('setupPasswordLockerCredential', 'setupPasswordLockerCredentialResult', {
    password: passwordLockerSetupPassword.value
  })
  passwordLockerSetupPassword.value = ''
  passwordLockerSetupPasswordConfirm.value = ''
  passwordLockerConfigured.value = true
  props.showToast(t('setupSuccess', props.lang), 'success')
  refreshPasswordLockerList()
}

// ---- 驗證 session ----

// 密碼庫的分頁內驗證 session 是後端權威，這裡的 passwordLockerSessionExpiresAt 只是前端自己
// 估算「大概還沒過期，先別急著彈驗證窗」，就算估算錯了，後端還是會用 PASSWORD_LOCKER_NOT_VERIFIED
// 擋下來，呼叫端要能處理這個情況。
function isPasswordLockerSessionLikelyValid() {
  return Date.now() < passwordLockerSessionExpiresAt.value
}

function markPasswordLockerSessionVerified() {
  passwordLockerSessionExpiresAt.value = Date.now() + passwordLockerSessionTimeoutMinutes.value * 60000
}

// 驗證通過（或前端判斷 session 還有效）之後要接著做的事，集中在這裡執行，
// 呼叫端只需要準備好 pendingAction 丟給 ensurePasswordLockerVerified。
async function runPasswordLockerAction(action) {
  if (action.type === 'reveal') {
    const result = await requestPasswordLockerMessage('revealPasswordLockerPassword', 'revealPasswordLockerPasswordResult', { id: action.id })
    if (result.success) {
      passwordLockerRevealedPasswords.value = { ...passwordLockerRevealedPasswords.value, [action.id]: result.password }
      if (action.editAfterReveal) {
        // 編輯表單需要看到完整內容才能改，帳號被遮蔽時額外多解密一次帳號、備註固定要解密
        // 一次——這是唯一會在同一個動作裡把多個密文欄位一起解開的地方。
        const decryptedUsername = action.item.usernameHidden
          ? (await requestPasswordLockerMessage('revealPasswordLockerUsername', 'revealPasswordLockerUsernameResult', { id: action.id })).username
          : null
        const notesResult = await requestPasswordLockerMessage('revealPasswordLockerNotes', 'revealPasswordLockerNotesResult', { id: action.id })
        openPasswordLockerFormWithItem(action.item, result.password, decryptedUsername, notesResult.success ? notesResult.notes : '')
      }
      if (action.showAfterReveal) {
        passwordLockerVisibleIds.value = new Set(passwordLockerVisibleIds.value).add(action.id)
      }
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      openPasswordLockerVerify(action)
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('verifyFailed', props.lang)))
    }
  } else if (action.type === 'revealUsername') {
    const result = await requestPasswordLockerMessage('revealPasswordLockerUsername', 'revealPasswordLockerUsernameResult', { id: action.id })
    if (result.success) {
      passwordLockerRevealedUsernames.value = { ...passwordLockerRevealedUsernames.value, [action.id]: result.username }
      passwordLockerUsernameVisibleIds.value = new Set(passwordLockerUsernameVisibleIds.value).add(action.id)
      await copyToClipboardWithAutoClear(result.username)
      props.showToast(t('usernameCopied', props.lang), 'success')
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      openPasswordLockerVerify(action)
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('verifyFailed', props.lang)))
    }
  } else if (action.type === 'copy') {
    const result = await requestPasswordLockerMessage('revealPasswordLockerPassword', 'revealPasswordLockerPasswordResult', { id: action.id })
    if (result.success) {
      await copyToClipboardWithAutoClear(result.password)
      props.showToast(t('copied', props.lang), 'success')
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      openPasswordLockerVerify(action)
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('verifyFailed', props.lang)))
    }
  } else if (action.type === 'revealTotp') {
    const result = await requestPasswordLockerMessage('revealPasswordLockerTotp', 'revealPasswordLockerTotpResult', { id: action.id })
    if (result.success) {
      const code = await computeTotpCode(result.secret, result.algorithm, result.digits, result.periodSeconds)
      passwordLockerRevealedTotps.value = {
        ...passwordLockerRevealedTotps.value,
        [action.id]: { secret: result.secret, algorithm: result.algorithm, digits: result.digits, period: result.periodSeconds, code }
      }
      startPasswordLockerTotpRefreshTimer()
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      // 沒有經過 ensurePasswordLockerVerified 的 session 檢查（TOTP 要求每次都重新驗證），
      // 這裡收到 NOT_VERIFIED 一律重跳驗證彈窗，不會有「其實剛剛才驗證過」這種誤判。
      openPasswordLockerVerify(action)
    } else if (result.errorCode === 'PASSWORD_LOCKER_TOTP_NOT_CONFIGURED') {
      props.showToast(t('totpNotConfigured', props.lang))
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('totpRevealFailed', props.lang)))
    }
  } else if (action.type === 'delete') {
    await finishPasswordLockerDelete(action.ids)
  } else if (action.type === 'save') {
    await finishPasswordLockerSave()
  } else if (action.type === 'changePassword') {
    const result = await requestPasswordLockerMessage('changePasswordLockerPassword', 'changePasswordLockerPasswordResult', { newPassword: action.newPassword })
    if (result.success) {
      props.showToast(t('changePasswordSuccess', props.lang), 'success')
      passwordLockerChangePasswordState.value = null
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('changePasswordFailed', props.lang)))
    }
  } else if (action.type === 'setupPasskey') {
    // 驗證剛通過、session 現在有效了，重新呼叫一次原本的動作——這次 backend 拿得到金鑰，
    // 會直接觸發真正的 Passkey 設定流程，不需要使用者自己再手動點一次按鈕。
    await setupPasswordLockerPasskeyAction()
  } else if (action.type === 'setupRecoveryKey') {
    await performPasswordLockerRecoveryKeySetup()
  } else if (action.type === 'openAssociatePicker') {
    passwordLockerPickerVisible.value = true
  } else if (action.type === 'exportCsv') {
    const result = await requestPasswordLockerMessage('exportPasswordLockerCsv', 'exportPasswordLockerCsvResult', {})
    if (result.success) {
      const saveResult = await requestPasswordLockerMessage('savePasswordLockerCsvToFile', 'savePasswordLockerCsvToFileResult', { content: result.csv })
      if (saveResult.success) {
        props.showToast(t('exportCsvSuccess', props.lang), 'success')
      } else if (!saveResult.cancelled) {
        props.showToast(props.translateError(saveResult.errorCode, saveResult.errorDetail, t('exportCsvFailed', props.lang)))
      }
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      openPasswordLockerVerify(action)
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('exportCsvFailed', props.lang)))
    }
  } else if (action.type === 'saveEncryptedFilesToLocker') {
    let savedCount = 0
    for (const item of action.items) {
      const result = await requestPasswordLockerMessage('addOrUpdatePasswordLockerCredential', 'addOrUpdatePasswordLockerCredentialResult', {
        category: 'EncryptedFile',
        title: item.path.split(/[\\/]/).pop(),
        domains: [],
        username: '',
        password: action.password,
        linkedVaultItemUuid: item.uuid
      })
      if (result.success) {
        savedCount++
      }
    }
    if (savedCount > 0) {
      props.showToast(t('saveEncryptedFilesSuccess', props.lang, { count: savedCount }), 'success')
    }
  } else if (action.type === 'importCsv') {
    const result = await requestPasswordLockerMessage('pickAndImportPasswordLockerCsv', 'importPasswordLockerCsvResult', {})
    if (result.success) {
      props.showToast(t('importCsvSuccess', props.lang, { imported: result.importedCount, skipped: result.skippedCount }), 'success')
      // 分開跳第二則 toast（不是併進同一句），讓「這是明文檔案，記得刪除」這個安全提醒
      // 保有自己的視覺份量，不會被匯入筆數這種例行性資訊稀釋掉。
      props.showToast(t('importCsvDeleteReminder', props.lang))
      await refreshPasswordLockerList()
    } else if (result.cancelled) {
      // 使用者自己取消選檔，不是失敗，不用顯示任何訊息。
    } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
      openPasswordLockerVerify(action)
    } else {
      props.showToast(props.translateError(result.errorCode, result.errorDetail, t('importCsvFailed', props.lang)))
    }
  }
}

// 匯出前先跳明確提示告知這是明文內容，確認後才走驗證流程——這個提示本身不算驗證的一部分，
// 就算 session 還沒過期一樣要先看到這個提示才能繼續。匯出是一次把整個密碼庫的明文內容整份
// 取出，風險比單筆顯示/複製高很多，這裡刻意不沿用共用的驗證 session（把
// passwordLockerSessionExpiresAt 歸零強制視為過期）——每次匯出都要求重新驗證一次。
async function exportPasswordLockerCsvAction() {
  const confirmed = await props.askConfirm(t('exportCsvWarning', props.lang), { confirmLabel: t('exportCsvConfirm', props.lang) })
  if (!confirmed) {
    return
  }
  passwordLockerSessionExpiresAt.value = 0
  await ensurePasswordLockerVerified({ type: 'exportCsv' })
}

async function importPasswordLockerCsvAction() {
  await ensurePasswordLockerVerified({ type: 'importCsv' })
}

// 加密流程結束時詢問要不要把這次用的密碼存進密碼庫——只在密碼庫這個可選配部件已安裝「而且」
// 使用者已經設定過的前提下才問，還沒裝／還沒設定的人不會突然被帶去設定精靈。這個函式就是
// defineExpose 裡 offerSaveEncryptedFiles 這個對外介面的真正實作（見 ADR-0004）。
async function offerSaveEncryptedFilesToLocker(password, items) {
  if (items.length === 0) {
    return
  }
  const status = await refreshPasswordLockerModuleStatus()
  if (status !== 'ok') {
    return
  }
  await refreshPasswordLockerList()
  if (!passwordLockerConfigured.value) {
    return
  }

  const confirmed = await props.askConfirm(
    items.length === 1
      ? t('saveEncryptedFilePrompt', props.lang)
      : t('saveEncryptedFilesPrompt', props.lang, { count: items.length }),
    { confirmLabel: t('saveEncryptedFilesConfirm', props.lang), cancelLabel: t('saveEncryptedFilesSkip', props.lang) }
  )
  if (!confirmed) {
    return
  }

  await ensurePasswordLockerVerified({ type: 'saveEncryptedFilesToLocker', items, password })
}

// 顯示/複製/刪除/儲存共用：session 前端估算還有效就直接做；沒有的話，Passkey 已設定就先
// 靜默試一次 Passkey（不先跳密碼欄位），失敗/取消才退回密碼彈窗——不能兩者都做（先跳密碼欄位、
// 送出時後端又預設再試一次 Passkey），那樣使用者要連續應付兩次驗證。
async function ensurePasswordLockerVerified(action) {
  if (isPasswordLockerSessionLikelyValid()) {
    await runPasswordLockerAction(action)
    return
  }
  // 已經有一個驗證流程在跑（Passkey 提示開著，或密碼彈窗已經開著）就不要再疊一個——
  // 沒有這道防線，連續點擊會讓每次點擊各自觸發一次獨立的 Windows Hello 提示。
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  if (passwordLockerPasskeyEnabled.value) {
    isPasswordLockerAuthBusy.value = true
    let result
    try {
      result = await requestPasswordLockerMessage('verifyPasswordLocker', 'verifyPasswordLockerResult', {})
    } finally {
      isPasswordLockerAuthBusy.value = false
    }
    if (result.success) {
      markPasswordLockerSessionVerified()
      await runPasswordLockerAction(action)
      return
    }
  }
  openPasswordLockerVerify(action)
}

function openPasswordLockerVerify(pendingAction) {
  passwordLockerVerifyState.value = { usingRecoveryKey: false, pendingAction }
  passwordLockerVerifyValue.value = ''
}

function cancelPasswordLockerVerify() {
  passwordLockerVerifyState.value = null
  passwordLockerVerifyValue.value = ''
}

// 密碼欄位已經開著、使用者想改用 Passkey 重試——不用整個取消再重新觸發一次原本的動作。
async function retryPasswordLockerVerifyPasskey() {
  const state = passwordLockerVerifyState.value
  if (!state || isPasswordLockerAuthBusy.value) {
    return
  }
  isPasswordLockerAuthBusy.value = true
  let result
  try {
    result = await requestPasswordLockerMessage('verifyPasswordLocker', 'verifyPasswordLockerResult', {})
  } finally {
    isPasswordLockerAuthBusy.value = false
  }
  if (!result.success) {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('verifyFailed', props.lang)))
    return
  }
  markPasswordLockerSessionVerified()
  const pendingAction = state.pendingAction
  passwordLockerVerifyState.value = null
  passwordLockerVerifyValue.value = ''
  if (pendingAction) {
    await runPasswordLockerAction(pendingAction)
  }
}

async function submitPasswordLockerVerify() {
  const state = passwordLockerVerifyState.value
  const value = passwordLockerVerifyValue.value
  if (!state || !value) {
    return
  }

  // tryPasskeyFirst: false——這裡是密碼欄位，使用者已經在打密碼了，不要讓後端又默默跳一次
  // Passkey 提示（Passkey 路徑已經在 ensurePasswordLockerVerified 裡試過、失敗了才會走到這裡）。
  const result = state.usingRecoveryKey
    ? await requestPasswordLockerMessage('verifyPasswordLockerByRecoveryKey', 'verifyPasswordLockerByRecoveryKeyResult', { recoveryKey: value })
    : await requestPasswordLockerMessage('verifyPasswordLocker', 'verifyPasswordLockerResult', { password: value, tryPasskeyFirst: false })

  if (!result.success) {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('verifyFailed', props.lang)))
    return
  }

  markPasswordLockerSessionVerified()
  const pendingAction = state.pendingAction
  passwordLockerVerifyState.value = null
  passwordLockerVerifyValue.value = ''
  if (pendingAction) {
    await runPasswordLockerAction(pendingAction)
  }
}

function openPasswordLockerChangePasswordForm() {
  passwordLockerChangePasswordState.value = { newPassword: '', confirm: '' }
}

function closePasswordLockerChangePasswordForm() {
  passwordLockerChangePasswordState.value = null
}

async function submitPasswordLockerChangePassword() {
  const state = passwordLockerChangePasswordState.value
  if (!state.newPassword) {
    props.showToast(t('passwordRequired', props.lang))
    return
  }
  if (state.newPassword !== state.confirm) {
    props.showToast(t('passwordMismatch', props.lang))
    return
  }
  await ensurePasswordLockerVerified({ type: 'changePassword', newPassword: state.newPassword })
}

async function setupPasswordLockerPasskeyAction() {
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  isPasswordLockerAuthBusy.value = true
  let result
  try {
    result = await requestPasswordLockerMessage('setupPasswordLockerPasskey', 'setupPasswordLockerPasskeyResult', {})
  } finally {
    isPasswordLockerAuthBusy.value = false
  }
  if (result.success) {
    passwordLockerPasskeyEnabled.value = true
    props.showToast(t('passkeySetupSuccess', props.lang), 'success')
  } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
    openPasswordLockerVerify({ type: 'setupPasskey' })
  } else {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('passkeySetupFailed', props.lang)))
  }
}

// 停用 Passkey 一樣要先驗證身份，但刻意保留「Passkey 驗證失敗就退回密碼」的逃生門——先靜默試
// 一次 Passkey，失敗/取消才退回這個套件自己的簡化版密碼提示彈窗（見檔案開頭
// simplifiedPasswordPromptState 的說明：不再共用 host 的 passwordPromptContext，那個彈窗
// 服務的是密碼庫以外的情境）。
async function disablePasswordLockerPasskeyAction() {
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  const confirmed = await props.askConfirm(t('passkeyDisableConfirm', props.lang), { variant: 'danger' })
  if (!confirmed) {
    return
  }
  isPasswordLockerAuthBusy.value = true
  let result
  try {
    result = await requestPasswordLockerMessage('disablePasswordLockerPasskey', 'disablePasswordLockerPasskeyResult', {})
  } finally {
    isPasswordLockerAuthBusy.value = false
  }
  if (result.success) {
    passwordLockerPasskeyEnabled.value = false
    props.showToast(t('passkeyDisabled', props.lang), 'success')
    return
  }
  const password = await openSimplifiedPasswordPrompt('disablePasskey')
  if (!password) {
    return
  }
  // tryPasskeyFirst: false——使用者已經在打密碼了，靜默 Passkey 那次嘗試已經在上面做過、
  // 失敗了才會走到這裡，不要讓後端又默默再跳一次 Windows Hello 提示。
  const passwordResult = await requestPasswordLockerMessage('disablePasswordLockerPasskey', 'disablePasswordLockerPasskeyResult', {
    password, tryPasskeyFirst: false
  })
  if (passwordResult.success) {
    passwordLockerPasskeyEnabled.value = false
    props.showToast(t('passkeyDisabled', props.lang), 'success')
  } else {
    props.showToast(props.translateError(passwordResult.errorCode, passwordResult.errorDetail, t('passkeyDisableFailed', props.lang)))
  }
}

// 已經有一組恢復金鑰的話，重新產生會讓舊的那組立刻失效（後端整筆覆蓋）——先跟使用者確認清楚，
// 避免使用者以為「再設定一次」是疊加、結果手上抄著的舊金鑰突然不能用了都不知道。
//
// 重新產生恢復金鑰算是重大操作，跟「顯示/複製某一筆密碼」這類日常操作不該共用同一段免驗證
// 時間——即使分頁的驗證 session 現在還沒到期，這裡也一律強制跳出驗證彈窗。
async function setupPasswordLockerRecoveryKeyAction() {
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  if (passwordLockerRecoveryKeyEnabled.value) {
    const confirmed = await props.askConfirm(t('recoveryKeyRegenerateConfirm', props.lang), { variant: 'danger' })
    if (!confirmed) {
      return
    }
  }
  openPasswordLockerVerify({ type: 'setupRecoveryKey' })
}

// 驗證彈窗通過之後才會執行到這裡，真正呼叫後端產生新的恢復金鑰。
async function performPasswordLockerRecoveryKeySetup() {
  isPasswordLockerAuthBusy.value = true
  let result
  try {
    result = await requestPasswordLockerMessage('setupPasswordLockerRecoveryKey', 'setupPasswordLockerRecoveryKeyResult', {})
  } finally {
    isPasswordLockerAuthBusy.value = false
  }
  if (result.success) {
    passwordLockerRecoveryKeyEnabled.value = true
    passwordLockerRecoveryKeyDisplay.value = result.recoveryKey
    passwordLockerRecoveryKeySaveState.value = ''
  } else if (result.errorCode === 'PASSWORD_LOCKER_NOT_VERIFIED') {
    openPasswordLockerVerify({ type: 'setupRecoveryKey' })
  } else {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('recoveryKeySetupFailed', props.lang)))
  }
}

function acknowledgePasswordLockerRecoveryKey() {
  passwordLockerRecoveryKeyDisplay.value = ''
  passwordLockerRecoveryKeySaveState.value = ''
}

// 跟 disablePasswordLockerPasskeyAction 同樣的理由：Passkey 已設定就先靜默試一次，
// 失敗/取消才退回這個套件自己的簡化版密碼提示彈窗，不要兩種驗證方式疊在一起要求使用者各做一次。
async function disablePasswordLockerRecoveryKeyAction() {
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  const confirmed = await props.askConfirm(t('recoveryKeyDisableConfirm', props.lang), { variant: 'danger' })
  if (!confirmed) {
    return
  }
  if (passwordLockerPasskeyEnabled.value) {
    isPasswordLockerAuthBusy.value = true
    let result
    try {
      result = await requestPasswordLockerMessage('disablePasswordLockerRecoveryKey', 'disablePasswordLockerRecoveryKeyResult', {})
    } finally {
      isPasswordLockerAuthBusy.value = false
    }
    if (result.success) {
      passwordLockerRecoveryKeyEnabled.value = false
      props.showToast(t('recoveryKeyDisabled', props.lang), 'success')
      return
    }
  }
  const password = await openSimplifiedPasswordPrompt('disableRecoveryKey')
  if (!password) {
    return
  }
  const passwordResult = await requestPasswordLockerMessage('disablePasswordLockerRecoveryKey', 'disablePasswordLockerRecoveryKeyResult', {
    password, tryPasskeyFirst: false
  })
  if (passwordResult.success) {
    passwordLockerRecoveryKeyEnabled.value = false
    props.showToast(t('recoveryKeyDisabled', props.lang), 'success')
  } else {
    props.showToast(props.translateError(passwordResult.errorCode, passwordResult.errorDetail, t('recoveryKeyDisableFailed', props.lang)))
  }
}

// ---- 清單頁：分組/排序/搜尋 ----

// 搜尋觸發備註比對：debounce 避免每打一個字就打一次後端；後端沒有有效 session 時本來就會
// 安靜回傳空陣列，這裡不用額外判斷「有沒有驗證過」。
watch(passwordLockerSearchQuery, (query) => {
  clearTimeout(passwordLockerSearchDebounceTimer)
  const trimmed = query.trim()
  if (!trimmed) {
    passwordLockerNotesMatchIds.value = new Set()
    return
  }
  passwordLockerSearchDebounceTimer = setTimeout(async () => {
    const result = await requestPasswordLockerMessage('searchPasswordLockerNotes', 'searchPasswordLockerNotesResult', { query: trimmed })
    passwordLockerNotesMatchIds.value = new Set(result.ids)
  }, 300)
})

// 同義詞群組：使用者搜尋「郵件」這種泛稱類別詞的時候，標題／網域欄位裡實際存的常常是
// 「Gmail」「Outlook」這類具體服務名稱，兩者字面上不會互相包含，純子字串比對搜不到。
// 群組內任何一個詞都視為互相同義，搜尋其中一個詞時，比對條件同時展開成整組詞的 OR。
const PASSWORD_LOCKER_SEARCH_SYNONYM_GROUPS = [
  ['信箱', '郵件', 'email', 'mail', 'gmail', 'outlook', 'yahoo', 'hotmail', 'icloud'],
  ['存簿', '銀行', '戶頭', '銀行帳戶', 'bank', 'passbook', '提款卡', 'atm'],
  ['電話', '電話號碼', '手機', '手機號碼', '門號', 'phone', 'mobile'],
  ['社群', '社交', 'facebook', 'instagram', 'threads', 'line', 'x', 'twitter'],
  ['購物', '網購', 'shopping', '蝦皮', 'shopee', 'momo', 'amazon', 'pchome'],
  ['影音', '串流', 'streaming', 'netflix', 'youtube', 'disney', 'spotify']
]

function expandPasswordLockerSearchToken(token) {
  const group = PASSWORD_LOCKER_SEARCH_SYNONYM_GROUPS.find((g) => g.includes(token))
  return group ? group : [token]
}

// 拆成多個關鍵字，每個關鍵字只要在標題／帳號／關聯網域任一欄位裡出現就算數（不用照順序、
// 不用同一個欄位），比單純比對「整串完整包含」更容易搜到；符合備註內容的額外用
// passwordLockerNotesMatchIds 補上（見上面的 watch）。同義詞群組讓「郵件」這種泛稱也能
// 搜到「Gmail」這類實際存的具體服務名稱。
const passwordLockerFilteredItems = computed(() => {
  const query = passwordLockerSearchQuery.value.trim().toLowerCase()
  if (!query) {
    return passwordLockerItems.value
  }
  const tokens = query.split(/\s+/).filter(Boolean)
  return passwordLockerItems.value.filter((item) => {
    if (passwordLockerNotesMatchIds.value.has(item.id)) {
      return true
    }
    const haystack = [item.title, item.username, ...item.associatedDomains].join(' ').toLowerCase()
    return tokens.every((token) => expandPasswordLockerSearchToken(token).some((variant) => haystack.includes(variant)))
  })
})

function sortPasswordLockerItems(items, mode) {
  const sorted = [...items]
  if (mode === 'alphabetical') {
    // 排序要跟畫面上實際顯示的文字一致——標題留空的紀錄清單上顯示的是自動組合出來的
    // 網站名稱，不是空字串，用 item.title 排會讓這些紀錄全部被排到最前面，跟看到的
    // 順序對不起來。
    sorted.sort((a, b) => passwordLockerDisplayTitle(a).localeCompare(passwordLockerDisplayTitle(b)))
  } else {
    sorted.sort((a, b) => new Date(b.createdAtUtc) - new Date(a.createdAtUtc))
  }
  return sorted
}

const passwordLockerWebsiteItems = computed(() =>
  sortPasswordLockerItems(passwordLockerFilteredItems.value.filter((item) => item.category === 'Website'), passwordLockerWebsiteSort.value)
)
const passwordLockerFileItems = computed(() =>
  sortPasswordLockerItems(passwordLockerFilteredItems.value.filter((item) => item.category === 'EncryptedFile'), passwordLockerFileSort.value)
)

// 空狀態判斷要跟著顯示內容篩選（全部／網站／已加密檔案）走，不然篩到某個分類剛好沒有
// 任何項目時，畫面會整個空白、看不出「篩選條件下沒有資料」還是「還在載入」。
const passwordLockerVisibleItemCount = computed(() => {
  if (passwordLockerViewFilter.value === 'website') {
    return passwordLockerWebsiteItems.value.length
  }
  if (passwordLockerViewFilter.value === 'file') {
    return passwordLockerFileItems.value.length
  }
  return passwordLockerFilteredItems.value.length
})

// 已經解密過就純前端切換遮住/顯示，不用重新驗證、也不用重新呼叫後端解密；還沒解密過的話
// 走一般的驗證流程，驗證通過、拿到明文後直接切成顯示狀態（showAfterReveal），不用使用者
// 驗證完之後還要再點一次才看得到。
function togglePasswordLockerVisibility(item) {
  if (passwordLockerVisibleIds.value.has(item.id)) {
    const next = new Set(passwordLockerVisibleIds.value)
    next.delete(item.id)
    passwordLockerVisibleIds.value = next
    return
  }
  if (passwordLockerRevealedPasswords.value[item.id]) {
    passwordLockerVisibleIds.value = new Set(passwordLockerVisibleIds.value).add(item.id)
    return
  }
  ensurePasswordLockerVerified({ type: 'reveal', id: item.id, showAfterReveal: true })
}

// 帳號欄位的點擊手勢，跟密碼那顆眼睛圖示是兩套獨立邏輯：
// - 沒勾選隱藏：帳號本來就是明文，點一下只負責複製，沒有顯示/隱藏狀態可言。
// - 有勾選隱藏：第一次點擊＝（必要時先驗證）解密＋複製＋顯示；已顯示狀態下再點一次＝
//   只負責變回隱藏，不重新複製一次。
async function togglePasswordLockerUsernameVisibility(item) {
  if (!item.usernameHidden) {
    await copyToClipboardWithAutoClear(item.username)
    props.showToast(t('usernameCopied', props.lang), 'success')
    return
  }
  if (passwordLockerUsernameVisibleIds.value.has(item.id)) {
    const next = new Set(passwordLockerUsernameVisibleIds.value)
    next.delete(item.id)
    passwordLockerUsernameVisibleIds.value = next
    return
  }
  await ensurePasswordLockerVerified({ type: 'revealUsername', id: item.id })
}

// TOTP 比密碼／帳號嚴格：不透過 ensurePasswordLockerVerified 的 session 檢查那段（那個函式
// 會沿用還沒過期的既有 session），一律強制走一次完整驗證，後端也有獨立的新鮮度視窗雙重把關，
// 不是只靠前端這裡配合。但「強制重新驗證」不等於「一定要跳密碼輸入框」——比照
// ensurePasswordLockerVerified 的既有模式，設定過 Passkey 就先靜默試一次 Windows Hello，
// 失敗/取消才退回密碼彈窗。
async function togglePasswordLockerTotpVisibility(item) {
  if (passwordLockerRevealedTotps.value[item.id]) {
    hidePasswordLockerTotp(item.id)
    return
  }
  const action = { type: 'revealTotp', id: item.id }
  if (isPasswordLockerAuthBusy.value || passwordLockerVerifyState.value) {
    return
  }
  if (passwordLockerPasskeyEnabled.value) {
    isPasswordLockerAuthBusy.value = true
    let result
    try {
      result = await requestPasswordLockerMessage('verifyPasswordLocker', 'verifyPasswordLockerResult', {})
    } finally {
      isPasswordLockerAuthBusy.value = false
    }
    if (result.success) {
      markPasswordLockerSessionVerified()
      await runPasswordLockerAction(action)
      return
    }
  }
  openPasswordLockerVerify(action)
}

function hidePasswordLockerTotp(id) {
  const next = { ...passwordLockerRevealedTotps.value }
  delete next[id]
  passwordLockerRevealedTotps.value = next
  if (Object.keys(next).length === 0) {
    stopPasswordLockerTotpRefreshTimer()
  }
}

function startPasswordLockerTotpRefreshTimer() {
  if (passwordLockerTotpRefreshTimer) {
    return
  }
  passwordLockerTotpRefreshTimer = setInterval(async () => {
    passwordLockerTotpNowTick.value = Date.now()
    const entries = Object.entries(passwordLockerRevealedTotps.value)
    if (entries.length === 0) {
      return
    }
    const updated = { ...passwordLockerRevealedTotps.value }
    for (const [id, totp] of entries) {
      try {
        updated[id] = { ...totp, code: await computeTotpCode(totp.secret, totp.algorithm, totp.digits, totp.period) }
      } catch {
        // 單筆算碼失敗（理論上不該發生，密鑰在揭露當下就已經驗證過格式）不影響其他已展開
        // 的項目，維持該筆的舊值即可。
      }
    }
    passwordLockerRevealedTotps.value = updated
  }, 1000)
}

function stopPasswordLockerTotpRefreshTimer() {
  if (passwordLockerTotpRefreshTimer) {
    clearInterval(passwordLockerTotpRefreshTimer)
    passwordLockerTotpRefreshTimer = null
  }
}

function togglePasswordLockerSelected(id) {
  const next = new Set(passwordLockerSelectedIds.value)
  if (next.has(id)) {
    next.delete(id)
  } else {
    next.add(id)
  }
  passwordLockerSelectedIds.value = next
}

function cancelPasswordLockerSelection() {
  passwordLockerSelectedIds.value = new Set()
}

async function deleteSelectedPasswordLockerItems() {
  const ids = [...passwordLockerSelectedIds.value]
  if (ids.length === 0) {
    return
  }
  await ensurePasswordLockerVerified({ type: 'delete', ids })
}

async function finishPasswordLockerDelete(ids) {
  const confirmed = await props.askConfirm(t('deleteConfirm', props.lang, { count: ids.length }), { variant: 'danger' })
  if (!confirmed) {
    return
  }
  const result = await requestPasswordLockerMessage('deletePasswordLockerCredentials', 'deletePasswordLockerCredentialsResult', { ids })
  if (result.success) {
    passwordLockerSelectedIds.value = new Set()
    props.showToast(t('deleteSuccess', props.lang), 'success')
    refreshPasswordLockerList()
  } else {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('deleteFailed', props.lang)))
  }
}

// ---- 新增/編輯表單 ----

function openPasswordLockerAddForm() {
  passwordLockerFormState.value = {
    id: null,
    category: 'Website',
    title: '',
    domains: [],
    domainInput: '',
    username: '',
    usernameHidden: false,
    password: '',
    notes: '',
    linkedVaultItemUuid: null
  }
  passwordLockerTotpExistingHasTotp.value = false
  passwordLockerTotpDraft.value = null
  passwordLockerTotpQrError.value = ''
  // vaultItems／refreshList 是選填 props（見 props 定義的說明）——沒提供就不用管，
  // 表單裡「已加密檔案」的連結欄位本來就會因為 props.vaultItems == null 而整個隱藏。
  if (props.vaultItems && props.refreshList && props.vaultItems.length === 0) {
    props.refreshList()
  }
}

// ---- 表單裡的 TOTP 區塊 ----

function setPasswordLockerTotpDraft(parsed) {
  passwordLockerTotpDraft.value = { secret: parsed.secret, algorithm: parsed.algorithm, digits: parsed.digits, period: parsed.period }
  startPasswordLockerTotpPreview()
}

// 使用者按「移除 TOTP」——空字串是後端 AddOrUpdateCredentialAsync 認得的清空信號，跟「這次
// 存檔不動 TOTP」（totpDraft 是 null）語意不同，不能混用。
function removePasswordLockerTotpDraft() {
  passwordLockerTotpDraft.value = { secret: '', algorithm: 'SHA1', digits: 6, period: 30 }
  passwordLockerTotpExistingHasTotp.value = false
  stopPasswordLockerTotpPreview()
  passwordLockerTotpPreviewCode.value = ''
}

async function handlePasswordLockerTotpQrFile(event) {
  const file = event.target.files?.[0]
  event.target.value = '' // 允許使用者選同一個檔案兩次都能觸發 change
  if (!file) {
    return
  }
  passwordLockerTotpQrError.value = ''
  try {
    const bitmap = await createImageBitmap(file)
    const canvas = document.createElement('canvas')
    canvas.width = bitmap.width
    canvas.height = bitmap.height
    const ctx = canvas.getContext('2d')
    ctx.drawImage(bitmap, 0, 0)
    const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height)
    const decoded = jsQR(imageData.data, imageData.width, imageData.height)
    const parsed = decoded ? parseTotpInput(decoded.data) : null
    if (!parsed) {
      passwordLockerTotpQrError.value = t('totpQrDecodeFailed', props.lang)
      return
    }
    setPasswordLockerTotpDraft(parsed)
  } catch {
    passwordLockerTotpQrError.value = t('totpQrDecodeFailed', props.lang)
  }
}

// 'input'（不是 'change'）——使用者貼上或打完密鑰後不用再多按 Enter 或點到外面，只要看起來
// 「打完了」（isTotpInputComplete，避免打到一半就被強制跳走）就直接切到預覽畫面。
function handlePasswordLockerTotpManualInput(text) {
  passwordLockerTotpQrError.value = ''
  if (!text.trim()) {
    passwordLockerTotpDraft.value = null
    stopPasswordLockerTotpPreview()
    return
  }
  if (!isTotpInputComplete(text)) {
    return
  }
  const parsed = parseTotpInput(text)
  if (!parsed) {
    passwordLockerTotpDraft.value = null
    stopPasswordLockerTotpPreview()
    return
  }
  setPasswordLockerTotpDraft(parsed)
}

async function startPasswordLockerTotpPreview() {
  stopPasswordLockerTotpPreview()
  const tick = async () => {
    passwordLockerTotpNowTick.value = Date.now()
    const draft = passwordLockerTotpDraft.value
    if (!draft || !draft.secret) {
      return
    }
    try {
      passwordLockerTotpPreviewCode.value = await computeTotpCode(draft.secret, draft.algorithm, draft.digits, draft.period)
    } catch {
      // 密鑰格式有問題（例如手動輸入貼了非 Base32 字元）——預覽區塊留空，不噴錯誤打斷輸入，
      // 使用者還在打字的過程本來就會經過不完整/不合法的中間狀態。
      passwordLockerTotpPreviewCode.value = ''
    }
  }
  await tick()
  passwordLockerTotpPreviewTimer = setInterval(tick, 1000)
}

function stopPasswordLockerTotpPreview() {
  if (passwordLockerTotpPreviewTimer) {
    clearInterval(passwordLockerTotpPreviewTimer)
    passwordLockerTotpPreviewTimer = null
  }
}

// 圓形倒數的 SVG style——讀取 passwordLockerTotpNowTick.value 讓這個函式在模板裡被當成
// reactive 求值：tick 每秒更新一次，Vue 會偵測到這裡讀取了它，畫面就跟著每秒重繪一次圓環。
// 剩餘時間落在最後 1/3 週期時圓環變色提醒使用者碼快輪替——改成比例而不是固定秒數，
// 這樣不同 period（例如少見的 60 秒週期）觸發警示的時機點才會跟 30 秒週期的「最後 10 秒」
// 有一致的相對意義，不會因為 period 變長就變得太晚才提醒。
function totpRingStyle(period) {
  const now = passwordLockerTotpNowTick.value
  const remaining = totpSecondsRemaining(period, now)
  return {
    strokeDasharray: TOTP_RING_CIRCUMFERENCE,
    strokeDashoffset: totpRingOffset(period, now),
    stroke: remaining <= period / 3 ? 'var(--color-danger, #b14328)' : 'var(--color-accent, #a37e2c)'
  }
}

// 選了一個已加密項目就把標題帶入該項目的檔名——「已加密檔案」類別的標題本來就該跟著
// 連結的項目走，不用使用者自己輸入一次一模一樣的檔名。選回「未連結」不清空標題，讓使用者
// 自己決定要不要保留已輸入的文字。
function onPasswordLockerLinkedFileChange() {
  const state = passwordLockerFormState.value
  const item = (props.vaultItems || []).find((i) => i.uuid === state.linkedVaultItemUuid)
  if (item) {
    state.title = item.originalName
  }
}

async function openPasswordLockerEditForm(item) {
  await ensurePasswordLockerVerified({ type: 'reveal', id: item.id, item, editAfterReveal: true })
}

// 編輯情境專用：拿到解密後的密碼、組出完整表單狀態並打開——跟 openPasswordLockerAddForm
// 共用同一個 passwordLockerFormState 形狀，差別只在 id 有值、欄位帶入既有資料。
// decryptedUsername 只有 item.usernameHidden 為 true 時才會有值，沒隱藏的話 item.username
// 本來就是明文，直接用。
function openPasswordLockerFormWithItem(item, decryptedPassword, decryptedUsername = null, decryptedNotes = '') {
  passwordLockerFormState.value = {
    id: item.id,
    category: item.category,
    title: item.title,
    domains: [...item.associatedDomains],
    domainInput: '',
    username: decryptedUsername ?? item.username,
    usernameHidden: item.usernameHidden,
    password: decryptedPassword,
    notes: decryptedNotes,
    linkedVaultItemUuid: item.linkedVaultItemUuid || null
  }
  passwordLockerTotpExistingHasTotp.value = !!item.hasTotp
  passwordLockerTotpDraft.value = null
  passwordLockerTotpQrError.value = ''
  if (item.category === 'EncryptedFile' && props.vaultItems && props.refreshList && props.vaultItems.length === 0) {
    props.refreshList()
  }
}

function closePasswordLockerForm() {
  passwordLockerFormState.value = null
  stopPasswordLockerTotpPreview()
  passwordLockerTotpPreviewCode.value = ''
}

// 切成「已加密檔案」時關聯網站欄位會整個收起來，順手清掉已輸入的內容——不然欄位藏起來但
// 資料還留著，使用者看不到卻被悄悄存進這筆紀錄，之後切回「網站」又會無緣無故冒出來，
// 很容易搞不清楚資料哪來的。
function onPasswordLockerCategoryChange() {
  const state = passwordLockerFormState.value
  if (state.category === 'EncryptedFile') {
    state.domains = []
    state.domainInput = ''
  }
}

function addPasswordLockerDomain() {
  const state = passwordLockerFormState.value
  const domain = state.domainInput.trim()
  if (!domain || state.domains.includes(domain)) {
    state.domainInput = ''
    return
  }
  state.domains = [...state.domains, domain]
  state.domainInput = ''
}

function removePasswordLockerDomain(domain) {
  const state = passwordLockerFormState.value
  state.domains = state.domains.filter((d) => d !== domain)
}

// 產生的密碼很多網站不接受符號（甚至限制哪些符號可以用），改成純英數字、比照恢復金鑰／UUID
// 的「一組固定長度＋用 - 分段」格式，好讀好抄、也不會因為符號被某個網站的密碼規則拒絕。
function groupWithDashes(raw, groupSize = 5) {
  const groups = []
  for (let i = 0; i < raw.length; i += groupSize) {
    groups.push(raw.slice(i, i + groupSize))
  }
  return groups.join('-')
}

async function generatePasswordLockerPasswordAction() {
  const result = await requestPasswordLockerMessage('generatePasswordLockerPassword', 'generatePasswordLockerPasswordResult', {
    length: 20, includeSymbols: false
  })
  passwordLockerFormState.value.password = groupWithDashes(result.password)
  showPasswordLockerFormPassword.value = true
}

const passwordLockerFormStrength = computed(() => {
  const password = passwordLockerFormState.value?.password || ''
  if (!password) {
    return null
  }
  if (password.length < 8) {
    return 'Weak'
  }
  const varietyCount = [/[a-z]/, /[A-Z]/, /[0-9]/, /[^a-zA-Z0-9]/].filter((re) => re.test(password)).length
  if (varietyCount < 3) {
    return 'Weak'
  }
  return password.length >= 16 ? 'Strong' : 'Medium'
})

// 「這組密碼在密碼庫裡還有幾筆紀錄也在使用」（純資訊性、不阻擋儲存）——跟上面的強度不同，
// 重複使用要比對整個密碼庫的已存密文，沒辦法在前端純算，每次改動都要問後端一次；debounce
// 是為了不要每打一個字元就打一次 IPC。excludeId 排除正在編輯的那筆紀錄本身，不然編輯既有
// 帳密時「重複使用」永遠至少會算到自己。
const passwordLockerFormReuseCount = ref(0)
let passwordLockerReuseCheckTimer = null
watch(() => passwordLockerFormState.value?.password, (password) => {
  clearTimeout(passwordLockerReuseCheckTimer)
  if (!password) {
    passwordLockerFormReuseCount.value = 0
    return
  }
  passwordLockerReuseCheckTimer = setTimeout(async () => {
    const result = await requestPasswordLockerMessage('checkPasswordLockerPasswordReuse', 'checkPasswordLockerPasswordReuseResult', {
      password, excludeId: passwordLockerFormState.value?.id || null
    })
    passwordLockerFormReuseCount.value = result.reuseCount || 0
  }, 400)
})

// ---- 關聯到現有帳號：不建立新紀錄，直接把新網域併進被選中那筆既有憑證的
// AssociatedDomains（資料模型本來就支援一筆紀錄關聯多個網站）。獨立於「新增帳密」之外的
// 工具列入口，兩步驟：選現有帳號→輸入新網域＋選填自訂標題。 ----

// 跟其他改動操作一致，先確保驗證通過才看得到清單/能送出變更。
async function openPasswordLockerAssociateAction() {
  await ensurePasswordLockerVerified({ type: 'openAssociatePicker' })
}

function selectPasswordLockerAssociateTarget(item) {
  passwordLockerPickerVisible.value = false
  // titleInput 刻意留空，不是拿現有標題來預填——這一格只負責「這次要多接上去的那一小段」，
  // 系統會自動接在目前顯示標題後面（見 submitPasswordLockerAssociateDomain），使用者只要
  // 打新的那個網站名稱，不用自己把舊標題整段複製貼上再手動加。
  passwordLockerAssociateState.value = { item, domainInput: '', titleInput: '' }
}

async function submitPasswordLockerAssociateDomain() {
  const state = passwordLockerAssociateState.value
  const domain = state.domainInput.trim()
  if (!domain) {
    props.showToast(t('associateDomainRequired', props.lang))
    return
  }
  const item = state.item
  const label = state.titleInput.trim()
  // 沒填新標籤：維持原樣。有填新標籤：接在「目前實際顯示的標題」後面存成新的自訂標題——
  // 不管目前顯示的是使用者自己設過的標題，還是本來就是自動組合出來的網站清單，都從使用者
  // 「看到的那個文字」接下去，不是接在看不到的原始 item.title 後面。
  const newTitle = label ? `${passwordLockerDisplayTitle(item)}${t('domainsListSeparator', props.lang)}${label}` : item.title

  const passwordResult = await requestPasswordLockerMessage('revealPasswordLockerPassword', 'revealPasswordLockerPasswordResult', { id: item.id })
  if (!passwordResult.success) {
    props.showToast(props.translateError(passwordResult.errorCode, passwordResult.errorDetail, t('verifyFailed', props.lang)))
    return
  }
  // item.username 是清單 metadata，帳號被遮蔽時這裡只會是空字串——直接原樣送回去會把
  // 這筆既有紀錄的帳號悄悄清空。先解密拿到真正的值，跟密碼一樣的加密狀態原封不動地
  // 重新送回去（usernameHidden 也要帶上，不然後端預設會當作「取消隱藏」處理）。
  const username = item.usernameHidden
    ? (await requestPasswordLockerMessage('revealPasswordLockerUsername', 'revealPasswordLockerUsernameResult', { id: item.id })).username
    : item.username

  const result = await requestPasswordLockerMessage('addOrUpdatePasswordLockerCredential', 'addOrUpdatePasswordLockerCredentialResult', {
    id: item.id,
    category: item.category,
    title: newTitle,
    domains: [...new Set([...item.associatedDomains, domain])],
    username,
    usernameHidden: item.usernameHidden,
    password: passwordResult.password,
    linkedVaultItemUuid: item.linkedVaultItemUuid || null
  })
  if (result.success) {
    passwordLockerAssociateState.value = null
    await refreshPasswordLockerList()
    props.showToast(t('useExistingAssociateSuccess', props.lang), 'success')
  } else {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('saveFailed', props.lang)))
  }
}

// 標題欄位現在是「使用者自訂顯示名稱」，有填就直接用；沒填的話從關聯網站即時組出
// 「A、B，以及C」——不寫死存進資料庫，網站增減會自動反映，不用另外找時機重算。
// 字元預算（不是固定列 3 個）：先試著把全部都列出來，太長再改成「A、B、C 等 N 個網站」，
// 避免只是第一個網站名稱剛好很長，就整串被 CSS 省略號從奇怪的地方截斷。
function passwordLockerDomainsSummary(domains, charBudget = 20) {
  if (!domains || domains.length === 0) {
    return ''
  }
  if (domains.length === 1) {
    return domains[0]
  }
  const separator = t('domainsListSeparator', props.lang)
  const full = domains.slice(0, -1).join(separator) + t('domainsListFinalSeparator', props.lang) + domains[domains.length - 1]
  if (full.length <= charBudget) {
    return full
  }
  const shown = [domains[0]]
  for (let i = 1; i < domains.length; i++) {
    const candidate = [...shown, domains[i]].join(separator)
    const withSuffix = t('domainsSummarySuffix', props.lang, { list: candidate, count: domains.length })
    if (withSuffix.length > charBudget) {
      break
    }
    shown.push(domains[i])
  }
  return t('domainsSummarySuffix', props.lang, { list: shown.join(separator), count: domains.length })
}

function passwordLockerDisplayTitle(item) {
  if (item.title && item.title.trim()) {
    return item.title
  }
  return passwordLockerDomainsSummary(item.associatedDomains) || item.title
}

async function submitPasswordLockerForm() {
  const state = passwordLockerFormState.value
  // 關聯網站欄位要按 Enter 才會變成下面的標籤——使用者打完字直接按「儲存」的話，
  // 輸入框裡還沒提交的文字會被整個忽略掉，一筆都沒記到。存檔前先幫忙補一次提交，
  // 跟按 Enter 是同一個動作，只是不強迫使用者一定要記得按。要在檢查標題必填與否
  // 之前先做，不然使用者只打了網站、標題留空，會被誤判成「網站也是空的」而卡住。
  if (state.domainInput.trim()) {
    addPasswordLockerDomain()
  }
  // 標題現在是「自訂顯示名稱」，留空的話清單會自動用關聯網站組出顯示文字。只有「已加密檔案」
  // 類別（沒有網站可以組）或「網站」類別但一個關聯網站都沒填（組不出東西可顯示）才強制要填標題。
  const needsTitle = state.category === 'EncryptedFile' || state.domains.length === 0
  if (needsTitle && !state.title.trim()) {
    props.showToast(t('titleRequired', props.lang))
    return
  }
  if (!state.password) {
    props.showToast(t('passwordFieldRequired', props.lang))
    return
  }
  await ensurePasswordLockerVerified({ type: 'save' })
}

async function finishPasswordLockerSave() {
  const state = passwordLockerFormState.value
  const draft = passwordLockerTotpDraft.value
  const result = await requestPasswordLockerMessage('addOrUpdatePasswordLockerCredential', 'addOrUpdatePasswordLockerCredentialResult', {
    id: state.id,
    category: state.category,
    title: state.title.trim(),
    domains: state.domains,
    username: state.username,
    usernameHidden: state.usernameHidden,
    password: state.password,
    notes: state.notes || null,
    linkedVaultItemUuid: state.category === 'EncryptedFile' ? state.linkedVaultItemUuid : null,
    // draft 是 null 代表這次存檔不動 TOTP（不帶 totp 屬性，後端 updateTotp 保持 false，
    // 維持既有紀錄原樣）；draft 不是 null 時，不管是新密鑰還是「移除」（secret 空字串）都要
    // 明確帶上。
    ...(draft !== null ? { totp: draft } : {})
  })
  if (result.success) {
    props.showToast(t('saveSuccess', props.lang), 'success')
    passwordLockerFormState.value = null
    stopPasswordLockerTotpPreview()
    passwordLockerTotpPreviewCode.value = ''
    refreshPasswordLockerList()
  } else {
    props.showToast(props.translateError(result.errorCode, result.errorDetail, t('saveFailed', props.lang)))
  }
}

// 複製機密內容（密碼、恢復金鑰、TOTP 動態碼、帳號等）到剪貼簿、過一段時間自動清空——這類
// 內容留在剪貼簿裡風險不小（Windows 剪貼簿歷史紀錄會保留好幾筆之前複製過的內容，甚至可能
// 跨裝置同步），比照密碼管理工具的慣例自動清空，但只有在剪貼簿裡還是我們剛剛複製的這份內容時
// 才清，避免蓋掉使用者後來自己複製的別的東西。套件自帶一份獨立實作（不透過 host 注入），
// 純粹是 navigator.clipboard／setTimeout 的包裝，沒有 host 依賴。
async function copyToClipboardWithAutoClear(value, clearAfterMs = 45000) {
  await navigator.clipboard.writeText(value)
  setTimeout(async () => {
    try {
      const current = await navigator.clipboard.readText()
      if (current === value) {
        await navigator.clipboard.writeText('')
      }
    } catch {
      // 讀取剪貼簿失敗（例如視窗失去焦點時瀏覽器會擋）就算了，不強求。
    }
  }, clearAfterMs)
}

// Esc 關閉目前開啟的彈窗——照優先權由上而下檢查哪個彈窗開著就關掉哪個，正常情況下同時間
// 只會有一個開著，順序對應 App.vue 既有的全域 Escape 處理（拿掉密碼庫以外的分支）。
function handlePasswordLockerKeydown(event) {
  if (event.key !== 'Escape') {
    return
  }
  if (simplifiedPasswordPromptState.value) {
    cancelSimplifiedPasswordPrompt()
  } else if (passwordLockerVerifyState.value) {
    cancelPasswordLockerVerify()
  } else if (passwordLockerAssociateState.value) {
    passwordLockerAssociateState.value = null
  } else if (passwordLockerPickerVisible.value) {
    passwordLockerPickerVisible.value = false
  } else if (passwordLockerFormState.value) {
    closePasswordLockerForm()
  } else if (passwordLockerChangePasswordState.value) {
    closePasswordLockerChangePasswordForm()
  }
  // passwordLockerRecoveryKeyDisplay 刻意不放進來：跟 Vault 的恢復金鑰顯示彈窗一樣，
  // 要強制使用者先勾選「已經抄下」才能關閉，Esc 不該是繞過這個安全機制的後門
  // （沿用 App.vue 既有全域 Escape 處理上的同一條說明）。
}

onMounted(() => {
  // 這個元件本身就是「密碼庫這一頁」，不像 App.vue 有 watch(activeTab) 決定何時該刷新——
  // 掛載當下就是使用者看得到這頁的當下，直接查一次部件狀態／清單。
  refreshPasswordLockerList()
})

onUnmounted(() => {
  if (passwordLockerTotpRefreshTimer) {
    clearInterval(passwordLockerTotpRefreshTimer)
  }
  if (passwordLockerTotpPreviewTimer) {
    clearInterval(passwordLockerTotpPreviewTimer)
  }
})
</script>

<template>
  <div class="password-locker-page" @keydown="handlePasswordLockerKeydown">
    <!-- 密碼庫是可選配部件，畫面依偵測結果分三種：未安裝／已安裝正常運作／已安裝但損毀，
         彼此分開顯示，不要讓使用者以為「損毀」代表「從沒裝過」。moduleStatus 還沒查完
         （'unknown'）之前，先不顯示三者中的任何一個，避免畫面先閃一下「未安裝」的引導
         才又跳成清單，造成視覺閃爍。 -->
    <div v-if="passwordLockerModuleStatus === 'notInstalled'" class="empty-state-block empty-state-block--module">
      <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><circle cx="8" cy="8" r="4.25" stroke="currentColor" stroke-width="1.6"/><path d="M11 11l9.5 9.5M16.5 15.5l3-3M19 18l2.5-2.5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
      <p class="empty-state-block__text">{{ t('moduleNotInstalledText', props.lang) }}</p>
      <button class="button button--primary" @click="installPasswordLockerModuleAction" :disabled="isInstallingPasswordLockerModule" type="button">
        {{ isInstallingPasswordLockerModule ? t('moduleInstalling', props.lang) : t('moduleInstallButton', props.lang) }}
      </button>
    </div>

    <div v-else-if="passwordLockerModuleStatus === 'broken'" class="empty-state-block empty-state-block--module">
      <svg class="empty-state-block__icon empty-state-block__icon--danger" viewBox="0 0 24 24" fill="none"><path d="M12 9v4M12 17h.01M10.3 3.9 2.7 17.5A2 2 0 0 0 4.4 20.5h15.2a2 2 0 0 0 1.7-3L14 3.9a2 2 0 0 0-3.4 0Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
      <p class="empty-state-block__text">{{ t('moduleBrokenText', props.lang) }}</p>
      <button class="button button--primary" @click="installPasswordLockerModuleAction" :disabled="isInstallingPasswordLockerModule" type="button">
        {{ isInstallingPasswordLockerModule ? t('moduleInstalling', props.lang) : t('moduleReinstallButton', props.lang) }}
      </button>
    </div>

    <template v-else-if="passwordLockerModuleStatus === 'ok'">
      <h1 class="page-title">
        <svg class="page-title__icon page-title__icon--vault" viewBox="0 0 24 24" fill="none"><circle cx="8.5" cy="15.5" r="3.5" stroke="currentColor" stroke-width="1.8"/><path d="M11 13 19.5 4.5M19.5 4.5 22 7M17 6.5 19.5 9" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>
        {{ t('pageTitle', props.lang) }}
      </h1>
      <p class="hint-text">{{ t('pageDescription', props.lang) }}</p>

      <!-- 首次設定：只有密碼是新增第一筆前必須先完成的關卡，Passkey／恢復金鑰是下面
           帳密管理子區塊裡的獨立按鈕。 -->
      <section v-if="!passwordLockerConfigured" class="settings-section">
        <h3 class="settings-section__title">{{ t('setupTitle', props.lang) }}</h3>
        <p class="hint-text">{{ t('setupDescription', props.lang) }}</p>
        <div class="field">
          <label class="field__label">{{ t('passwordLabel', props.lang) }}</label>
          <div class="password-field">
            <input v-model="passwordLockerSetupPassword" :type="showPasswordLockerSetupPassword ? 'text' : 'password'" class="text-input" />
            <button
              type="button"
              class="password-field__toggle"
              :aria-label="t(showPasswordLockerSetupPassword ? 'hide' : 'show', props.lang)"
              @click="showPasswordLockerSetupPassword = !showPasswordLockerSetupPassword"
            >
              <svg v-if="showPasswordLockerSetupPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </button>
          </div>
        </div>
        <div class="field">
          <label class="field__label">{{ t('passwordConfirmLabel', props.lang) }}</label>
          <div class="password-field">
            <input v-model="passwordLockerSetupPasswordConfirm" :type="showPasswordLockerSetupPassword ? 'text' : 'password'" class="text-input" @keyup.enter="submitPasswordLockerSetup" />
            <button
              type="button"
              class="password-field__toggle"
              :aria-label="t(showPasswordLockerSetupPassword ? 'hide' : 'show', props.lang)"
              @click="showPasswordLockerSetupPassword = !showPasswordLockerSetupPassword"
            >
              <svg v-if="showPasswordLockerSetupPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </button>
          </div>
        </div>
        <button class="button button--primary" @click="submitPasswordLockerSetup" type="button">{{ t('setupSubmit', props.lang) }}</button>
      </section>

      <template v-else>
        <!-- 帳密清單／設定兩個內部小分頁——設定（改密碼／Passkey／恢復金鑰／CSV）原本併在
             清單最下面，改成獨立按鈕切換，不要一直黏在清單下面（重用 App.vue 已加密清單分頁
             既有的 .sub-tab-bar 藥丸樣式，不新造一套）。 -->
        <div class="sub-tab-bar">
          <button type="button" class="sub-tab-bar__item" :class="{ 'is-active': activeVaultSubTab === 'list' }" @click="activeVaultSubTab = 'list'">{{ t('subTabList', props.lang) }}</button>
          <button type="button" class="sub-tab-bar__item" :class="{ 'is-active': activeVaultSubTab === 'settings' }" @click="activeVaultSubTab = 'settings'">{{ t('subTabSettings', props.lang) }}</button>
        </div>

        <template v-if="activeVaultSubTab === 'list'">
        <!-- 選取模式下換成「取消選取／刪除選取」這兩顆按鈕。這一列固定不換行——按鈕數量在
             一般模式（3 顆）跟選取模式（2 顆）之間切換時，如果讓這一列自由換行，總寬度變化
             會讓搜尋框跟著換不換行，連帶讓整個表格跟著往上/下掉一列。篩選下拉獨立放到下一列，
             不受這一列按鈕數量變化影響。 -->
        <div class="button-row button-row--nowrap" v-if="passwordLockerSelectedIds.size === 0">
          <button class="button button--primary" @click="openPasswordLockerAddForm" type="button">{{ t('addButton', props.lang) }}</button>
          <button class="button button--secondary" @click="openPasswordLockerAssociateAction" type="button">{{ t('associateButton', props.lang) }}</button>
          <button class="button button--secondary" @click="refreshPasswordLockerList" :disabled="isLoadingPasswordLocker" type="button">
            {{ isLoadingPasswordLocker ? t('loading', props.lang) : t('refresh', props.lang) }}
          </button>
          <input
            v-model="passwordLockerSearchQuery"
            class="text-input"
            style="margin-left: auto; flex: 1 1 160px; min-width: 120px; max-width: 240px;"
            :placeholder="t('searchPlaceholder', props.lang)"
          />
        </div>
        <div class="button-row button-row--nowrap" v-else>
          <button class="button button--secondary" @click="cancelPasswordLockerSelection" type="button">{{ t('cancelSelectionButton', props.lang) }}</button>
          <button class="button button--danger" @click="deleteSelectedPasswordLockerItems" type="button">
            {{ t('deleteSelectedButton', props.lang) }} ({{ passwordLockerSelectedIds.size }})
          </button>
          <input
            v-model="passwordLockerSearchQuery"
            class="text-input"
            style="margin-left: auto; flex: 1 1 160px; min-width: 120px; max-width: 240px;"
            :placeholder="t('searchPlaceholder', props.lang)"
          />
        </div>
        <div class="button-row">
          <select v-model="passwordLockerViewFilter" class="select-input">
            <option value="all">{{ t('viewAll', props.lang) }}</option>
            <option value="website">{{ t('groupWebsite', props.lang) }}</option>
            <option value="file">{{ t('groupEncryptedFile', props.lang) }}</option>
          </select>
        </div>

        <div v-if="!isLoadingPasswordLocker && passwordLockerVisibleItemCount === 0" class="empty-state-block">
          <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><circle cx="8" cy="8" r="4.25" stroke="currentColor" stroke-width="1.6"/><path d="M11 11l9.5 9.5M16.5 15.5l3-3M19 18l2.5-2.5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
          <p class="empty-state-block__text">{{ passwordLockerSearchQuery ? t('noSearchResults', props.lang) : t('noItems', props.lang) }}</p>
        </div>

        <template v-for="group in [
          { key: 'website', label: t('groupWebsite', props.lang), items: passwordLockerWebsiteItems, sortRef: 'passwordLockerWebsiteSort' },
          { key: 'file', label: t('groupEncryptedFile', props.lang), items: passwordLockerFileItems, sortRef: 'passwordLockerFileSort' }
        ].filter((g) => passwordLockerViewFilter === 'all' || passwordLockerViewFilter === g.key)" :key="group.key">
          <div v-if="group.items.length > 0" class="table-scroll" style="margin-top: 20px; margin-bottom: 24px;">
            <div style="margin-bottom: 8px;">
              <h3 class="settings-section__title" style="margin: 0 0 0.4rem;">{{ group.label }}</h3>
              <select
                class="select-input select-input--compact"
                :value="group.key === 'website' ? passwordLockerWebsiteSort : passwordLockerFileSort"
                @change="group.key === 'website' ? (passwordLockerWebsiteSort = $event.target.value) : (passwordLockerFileSort = $event.target.value)"
              >
                <option value="alphabetical">{{ t('sortAlphabetical', props.lang) }}</option>
                <option value="time">{{ t('sortTime', props.lang) }}</option>
              </select>
            </div>
            <table class="table table--password-locker">
              <template v-if="group.key === 'website'">
                <colgroup>
                  <col style="width: 5%;" />
                  <col style="width: 10%;" />
                  <col style="width: 18%;" />
                  <col style="width: 22%;" />
                  <col style="width: 18%;" />
                  <col style="width: 27%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th></th>
                    <th>{{ t('colTitle', props.lang) }}</th>
                    <th>{{ t('colUsername', props.lang) }}</th>
                    <th>{{ t('colPassword', props.lang) }}</th>
                    <th>{{ t('colTotp', props.lang) }}</th>
                    <th></th>
                  </tr>
                </thead>
              </template>
              <!-- 已加密檔案類別不像 Website 一樣有帳號／TOTP 這兩個概念——這個類別純粹是
                   幫已加密檔案存一組密碼，不連結真實登入帳號，欄位跟著砍掉，「標題」也改標成
                   「檔案名」比較符合這個類別實際存的內容。 -->
              <template v-else>
                <colgroup>
                  <col style="width: 5%;" />
                  <col style="width: 43%;" />
                  <col style="width: 22%;" />
                  <col style="width: 30%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th></th>
                    <th>{{ t('colFileName', props.lang) }}</th>
                    <th>{{ t('colPassword', props.lang) }}</th>
                    <th></th>
                  </tr>
                </thead>
              </template>
              <tbody>
                <tr v-for="item in group.items" :key="item.id">
                  <td>
                    <input type="checkbox" :checked="passwordLockerSelectedIds.has(item.id)" @change="togglePasswordLockerSelected(item.id)" />
                  </td>
                  <td>
                    <div
                      class="cell-name"
                      :class="{ 'text-strikethrough': item.sourceDeleted }"
                      :title="item.sourceDeleted ? t('sourceDeletedLabel', props.lang) : passwordLockerDisplayTitle(item)"
                    >
                      {{ passwordLockerDisplayTitle(item) }}
                    </div>
                  </td>
                  <td v-if="group.key === 'website'">
                    <div
                      v-if="item.usernameHidden && !passwordLockerUsernameVisibleIds.has(item.id)"
                      class="cell-name cell-clickable"
                      style="max-width: 100%;"
                      role="button"
                      tabindex="0"
                      :title="t('usernameHiddenHint', props.lang)"
                      @click="togglePasswordLockerUsernameVisibility(item)"
                      @keydown.enter="togglePasswordLockerUsernameVisibility(item)"
                    >••••••••</div>
                    <div
                      v-else
                      class="cell-name cell-clickable"
                      style="max-width: 100%;"
                      role="button"
                      tabindex="0"
                      :title="item.usernameHidden ? passwordLockerRevealedUsernames[item.id] : item.username"
                      @click="togglePasswordLockerUsernameVisibility(item)"
                      @keydown.enter="togglePasswordLockerUsernameVisibility(item)"
                    >{{ item.usernameHidden ? passwordLockerRevealedUsernames[item.id] : item.username }}</div>
                  </td>
                  <td>
                    <div class="totp-cell">
                      <div
                        v-if="passwordLockerVisibleIds.has(item.id) && passwordLockerRevealedPasswords[item.id]"
                        class="cell-name text-input--mono"
                        style="max-width: calc(100% - 2ch);"
                        :title="passwordLockerRevealedPasswords[item.id]"
                      >{{ passwordLockerRevealedPasswords[item.id] }}</div>
                      <span v-else>••••••••</span>
                      <button
                        type="button"
                        class="password-field__toggle password-field__toggle--inline"
                        :aria-label="t(passwordLockerVisibleIds.has(item.id) ? 'hide' : 'show', props.lang)"
                        @click="togglePasswordLockerVisibility(item)"
                      >
                        <svg v-if="passwordLockerVisibleIds.has(item.id)" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                        <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
                      </button>
                    </div>
                  </td>
                  <td v-if="group.key === 'website'">
                    <div v-if="item.hasTotp" class="totp-cell">
                      <template v-if="passwordLockerRevealedTotps[item.id]">
                        <svg viewBox="0 0 36 36" class="totp-ring totp-ring--small">
                          <circle class="totp-ring__track" cx="18" cy="18" r="16" />
                          <circle class="totp-ring__progress" cx="18" cy="18" r="16" :style="totpRingStyle(passwordLockerRevealedTotps[item.id].period)" />
                        </svg>
                        <span
                          class="totp-cell__code text-input--mono"
                          role="button"
                          tabindex="0"
                          :title="t('totpCopyHint', props.lang)"
                          @click="copyToClipboardWithAutoClear(passwordLockerRevealedTotps[item.id].code)"
                          @keydown.enter="copyToClipboardWithAutoClear(passwordLockerRevealedTotps[item.id].code)"
                        >{{ passwordLockerRevealedTotps[item.id].code }}</span>
                      </template>
                      <button
                        type="button"
                        class="password-field__toggle password-field__toggle--inline"
                        :aria-label="t(passwordLockerRevealedTotps[item.id] ? 'hide' : 'totpShowButton', props.lang)"
                        @click="togglePasswordLockerTotpVisibility(item)"
                      >
                        <svg v-if="passwordLockerRevealedTotps[item.id]" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                        <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
                      </button>
                    </div>
                    <span v-else class="cell-empty">—</span>
                  </td>
                  <td>
                    <div class="table__actions">
                      <button class="button button--tiny" @click="ensurePasswordLockerVerified({ type: 'copy', id: item.id })" type="button">
                        {{ t('copy', props.lang) }}
                      </button>
                      <button class="button button--tiny" @click="openPasswordLockerEditForm(item)" type="button">
                        {{ t('editButton', props.lang) }}
                      </button>
                      <button class="button button--tiny" @click="ensurePasswordLockerVerified({ type: 'delete', ids: [item.id] })" type="button">
                        {{ t('deleteButton', props.lang) }}
                      </button>
                    </div>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
        </template>

        <!-- 設定小分頁：改密碼／Passkey／恢復金鑰／CSV 匯出入，原本併在清單最下面，改成
             獨立按鈕切換到這個小分頁，不要一直黏在清單下面（見上面 .sub-tab-bar）。 -->
        <template v-else>
          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('credentialTitle', props.lang) }}</h3>
            <!-- 密碼／Passkey／恢復金鑰各自獨立一塊，用分隔線隔開——三者是各自獨立的解鎖路徑，
                 混在同一排按鈕裡容易讓人以為彼此有關聯或互相依賴。 -->
            <div class="settings-subsection">
              <h4 class="settings-subsection__title">{{ t('passwordSectionLabel', props.lang) }}</h4>
              <button class="button button--secondary" @click="openPasswordLockerChangePasswordForm" type="button">
                {{ t('changePasswordButton', props.lang) }}
              </button>
            </div>

            <div class="settings-subsection">
              <h4 class="settings-subsection__title">{{ t('passkeySectionLabel', props.lang) }}</h4>
              <div class="button-row">
                <button class="button button--secondary" @click="setupPasswordLockerPasskeyAction" type="button">
                  {{ passwordLockerPasskeyEnabled ? t('passkeyResetupButton', props.lang) : t('passkeySetupButton', props.lang) }}
                </button>
                <button v-if="passwordLockerPasskeyEnabled" class="button button--secondary" @click="disablePasswordLockerPasskeyAction" type="button">
                  {{ t('passkeyDisableButton', props.lang) }}
                </button>
              </div>
            </div>

            <div class="settings-subsection">
              <h4 class="settings-subsection__title">{{ t('recoveryKeySectionLabel', props.lang) }}</h4>
              <div class="button-row">
                <button class="button button--secondary" @click="setupPasswordLockerRecoveryKeyAction" type="button">
                  {{ passwordLockerRecoveryKeyEnabled ? t('recoveryKeyResetupButton', props.lang) : t('recoveryKeySetupButton', props.lang) }}
                </button>
                <button v-if="passwordLockerRecoveryKeyEnabled" class="button button--secondary" @click="disablePasswordLockerRecoveryKeyAction" type="button">
                  {{ t('recoveryKeyDisableButton', props.lang) }}
                </button>
              </div>
            </div>

            <div class="settings-subsection">
              <h4 class="settings-subsection__title">{{ t('csvSectionLabel', props.lang) }}</h4>
              <div class="button-row">
                <button class="button button--secondary" @click="importPasswordLockerCsvAction" type="button">{{ t('importCsvButton', props.lang) }}</button>
                <button class="button button--secondary" @click="exportPasswordLockerCsvAction" type="button">{{ t('exportCsvButton', props.lang) }}</button>
              </div>
            </div>
          </section>

          <!-- 解除安裝部件是危險操作，移進設定小分頁裡，不要在帳密清單這個第一畫面就出現。 -->
          <section class="settings-section">
            <h4 class="settings-subsection__title">{{ t('moduleManagementSectionLabel', props.lang) }}</h4>
            <button class="button button--danger" @click="uninstallPasswordLockerModuleAction" type="button">
              {{ t('uninstallModuleButton', props.lang) }}
            </button>
          </section>
        </template>
      </template>
    </template>

    <!-- 密碼庫自己的簡化版密碼提示彈窗（停用 Passkey／停用恢復金鑰用）。 -->
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

    <!-- 密碼庫驗證彈窗：跟上面的簡化版密碼提示分開，因為這裡多了「改用恢復金鑰」的切換。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerVerifyState" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('verifyTitle', props.lang) }}</h2>
          <p class="modal__subtitle">{{ passwordLockerVerifyState.usingRecoveryKey ? t('verifyByRecoveryKeyPrompt', props.lang) : t('verifyPasswordPrompt', props.lang) }}</p>
          <div class="password-field">
            <input
              v-model="passwordLockerVerifyValue"
              :type="showPasswordLockerVerifyValue ? 'text' : 'password'"
              class="text-input"
              @keyup.enter="submitPasswordLockerVerify"
            />
            <button
              type="button"
              class="password-field__toggle"
              :aria-label="t(showPasswordLockerVerifyValue ? 'hide' : 'show', props.lang)"
              @click="showPasswordLockerVerifyValue = !showPasswordLockerVerifyValue"
            >
              <svg v-if="showPasswordLockerVerifyValue" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </button>
          </div>
          <div class="button-row">
            <button
              v-if="passwordLockerRecoveryKeyEnabled"
              class="link-button"
              type="button"
              @click="passwordLockerVerifyState.usingRecoveryKey = !passwordLockerVerifyState.usingRecoveryKey; passwordLockerVerifyValue = ''"
            >
              {{ passwordLockerVerifyState.usingRecoveryKey ? t('verifyByPasswordToggle', props.lang) : t('verifyByRecoveryKeyToggle', props.lang) }}
            </button>
            <!-- Passkey 已設定的話，第一次靜默嘗試（見 ensurePasswordLockerVerified）失敗/取消
                 才會走到這個密碼彈窗——這裡補一個能直接重試 Passkey 的按鈕，不用整個取消、
                 退出去再重新觸發一次原本的動作才能改用 Passkey。 -->
            <button
              v-if="passwordLockerPasskeyEnabled && !passwordLockerVerifyState.usingRecoveryKey"
              class="link-button"
              type="button"
              @click="retryPasswordLockerVerifyPasskey"
            >
              {{ t('retryPasskeyButton', props.lang) }}
            </button>
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" @click="cancelPasswordLockerVerify" type="button">{{ t('cancel', props.lang) }}</button>
            <button class="button button--primary" @click="submitPasswordLockerVerify" type="button" :disabled="!passwordLockerVerifyValue">
              {{ t('verifyTitle', props.lang) }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 密碼庫恢復金鑰顯示彈窗：只在這次呼叫回傳看得到，不留任何副本。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerRecoveryKeyDisplay" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('recoveryKeyDisplayTitle', props.lang) }}</h2>
          <p class="modal__subtitle">{{ t('recoveryKeyDisplayDescription', props.lang) }}</p>
          <textarea readonly rows="3" class="text-input text-input--mono">{{ passwordLockerRecoveryKeyDisplay }}</textarea>
          <label class="checkbox-field" style="margin-top: 12px;">
            <input type="checkbox" :checked="passwordLockerRecoveryKeySaveState === 'saved'" @change="passwordLockerRecoveryKeySaveState = $event.target.checked ? 'saved' : ''" />
            <span>{{ t('recoveryKeySavedConfirm', props.lang) }}</span>
          </label>
          <div class="modal__footer">
            <button class="button button--primary" @click="acknowledgePasswordLockerRecoveryKey" type="button" :disabled="passwordLockerRecoveryKeySaveState !== 'saved'">
              {{ t('recoveryKeyDone', props.lang) }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 密碼庫重設密碼：主金鑰不變，只是重新包一次，既有憑證不用重新輸入。跟新增/編輯表單
         同樣的疊層理由，驗證彈窗開著時暫時藏起來。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerChangePasswordState && !passwordLockerVerifyState" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('changePasswordButton', props.lang) }}</h2>
          <div class="field">
            <label class="field__label">{{ t('newPasswordLabel', props.lang) }}</label>
            <div class="password-field">
              <input
                v-model="passwordLockerChangePasswordState.newPassword"
                :type="showPasswordLockerChangePassword ? 'text' : 'password'"
                class="text-input"
              />
              <button
                type="button"
                class="password-field__toggle"
                :aria-label="t(showPasswordLockerChangePassword ? 'hide' : 'show', props.lang)"
                @click="showPasswordLockerChangePassword = !showPasswordLockerChangePassword"
              >
                <svg v-if="showPasswordLockerChangePassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
              </button>
            </div>
          </div>
          <div class="field">
            <label class="field__label">{{ t('passwordConfirmLabel', props.lang) }}</label>
            <div class="password-field">
              <input
                v-model="passwordLockerChangePasswordState.confirm"
                :type="showPasswordLockerChangePassword ? 'text' : 'password'"
                class="text-input"
                @keyup.enter="submitPasswordLockerChangePassword"
              />
              <button
                type="button"
                class="password-field__toggle"
                :aria-label="t(showPasswordLockerChangePassword ? 'hide' : 'show', props.lang)"
                @click="showPasswordLockerChangePassword = !showPasswordLockerChangePassword"
              >
                <svg v-if="showPasswordLockerChangePassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
              </button>
            </div>
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" @click="closePasswordLockerChangePasswordForm" type="button">{{ t('cancel', props.lang) }}</button>
            <button class="button button--primary" @click="submitPasswordLockerChangePassword" type="button">{{ t('saveButton', props.lang) }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 密碼庫新增/編輯表單。儲存時會先跳驗證彈窗（見 submitPasswordLockerForm →
         ensurePasswordLockerVerified）——這兩個彈窗在 DOM 上是先後兩個獨立的 .modal-overlay，
         同時顯示的話後面的會蓋住前面的，這裡驗證彈窗在後面，會蓋住這個表單，所以驗證彈窗開著
         的時候暫時把這個表單藏起來（狀態還在，不會遺失已填的內容），驗證完成或取消後再繼續。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerFormState && !passwordLockerVerifyState" class="modal-overlay">
        <div class="modal modal--form">
          <h2 class="modal__title">{{ passwordLockerFormState.id ? t('formEditTitle', props.lang) : t('formAddTitle', props.lang) }}</h2>

          <div class="field">
            <label class="field__label">{{ t('categoryLabel', props.lang) }}</label>
            <select class="select-input" v-model="passwordLockerFormState.category" @change="onPasswordLockerCategoryChange">
              <option value="Website">{{ t('categoryWebsite', props.lang) }}</option>
              <option value="EncryptedFile">{{ t('categoryEncryptedFile', props.lang) }}</option>
            </select>
          </div>

          <!-- 「已加密檔案」類別可以直接從已加密清單挑一個項目連結（linkedVaultItemUuid），
               挑選後標題跟著該項目的檔名走。只有 host 有提供 vaultItems 時才顯示這個欄位——
               沒提供就整塊隱藏，不假造一份空清單（見 props.vaultItems 的說明）。 -->
          <div v-if="passwordLockerFormState.category === 'EncryptedFile' && props.vaultItems" class="field">
            <label class="field__label">{{ t('linkedFileLabel', props.lang) }}</label>
            <select class="select-input" v-model="passwordLockerFormState.linkedVaultItemUuid" @change="onPasswordLockerLinkedFileChange">
              <option :value="null">{{ t('linkedFileNone', props.lang) }}</option>
              <option v-for="item in props.vaultItems" :key="item.uuid" :value="item.uuid">{{ item.originalName }}</option>
            </select>
          </div>

          <div class="field">
            <label class="field__label">{{ t('titleLabel', props.lang) }}</label>
            <input v-model="passwordLockerFormState.title" class="text-input" :placeholder="t('titlePlaceholder', props.lang)" />
            <p v-if="passwordLockerFormState.category === 'Website'" class="hint-text">{{ t('titleOptionalHint', props.lang) }}</p>
          </div>

          <!-- 關聯網站只對「網站」類別有意義（瀏覽器擴充功能靠網域比對這筆憑證），
               「已加密檔案」類別不會有瀏覽器情境，這個欄位對它沒有作用，切過去要跟著收起來。 -->
          <div v-if="passwordLockerFormState.category === 'Website'" class="field">
            <label class="field__label">{{ t('domainsLabel', props.lang) }}</label>
            <input
              v-model="passwordLockerFormState.domainInput"
              class="text-input"
              :placeholder="t('domainsPlaceholder', props.lang)"
              @keyup.enter="addPasswordLockerDomain"
            />
            <div v-if="passwordLockerFormState.domains.length > 0" class="button-row" style="margin-top: 8px;">
              <span v-for="domain in passwordLockerFormState.domains" :key="domain" class="tag">
                {{ domain }}
                <button type="button" class="tag__remove" @click="removePasswordLockerDomain(domain)" :aria-label="t('domainRemove', props.lang)">×</button>
              </span>
            </div>
          </div>

          <div class="field">
            <label class="field__label">{{ t('usernameLabel', props.lang) }}</label>
            <input v-model="passwordLockerFormState.username" class="text-input" />
            <label class="checkbox-field" style="margin-top: 8px;">
              <input type="checkbox" v-model="passwordLockerFormState.usernameHidden" />
              <span>{{ t('hideUsernameLabel', props.lang) }}</span>
            </label>
          </div>

          <div class="field">
            <label class="field__label">{{ t('passwordLabel', props.lang) }}</label>
            <div class="password-field">
              <input v-model="passwordLockerFormState.password" :type="showPasswordLockerFormPassword ? 'text' : 'password'" class="text-input" />
              <button
                type="button"
                class="password-field__toggle"
                :aria-label="t(showPasswordLockerFormPassword ? 'hide' : 'show', props.lang)"
                @click="showPasswordLockerFormPassword = !showPasswordLockerFormPassword"
              >
                <svg v-if="showPasswordLockerFormPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
              </button>
            </div>
            <p v-if="passwordLockerFormStrength" class="hint-text">
              {{ t('strengthLabel', props.lang) }}: {{ t(`strength${passwordLockerFormStrength}`, props.lang) }}
            </p>
            <p v-if="passwordLockerFormReuseCount > 0" class="hint-text text-warning-soft">
              {{ t('reuseWarning', props.lang, { count: passwordLockerFormReuseCount }) }}
            </p>
            <div class="button-row" style="margin-top: 8px;">
              <button class="button button--secondary button--tiny" @click="generatePasswordLockerPasswordAction" type="button">{{ t('generateButton', props.lang) }}</button>
            </div>
          </div>

          <div class="field">
            <label class="field__label">{{ t('notesLabel', props.lang) }}</label>
            <textarea v-model="passwordLockerFormState.notes" rows="2" class="text-input"></textarea>
          </div>

          <!-- TOTP 只有「網站」分類支援——已加密檔案沒有登入頁面這回事，動態驗證碼沒有意義。 -->
          <div v-if="passwordLockerFormState.category === 'Website'" class="field">
            <label class="field__label">{{ t('totpLabel', props.lang) }}</label>

            <!-- 狀態 1：既有紀錄本來就有設定、這次還沒動它 -->
            <div v-if="passwordLockerTotpExistingHasTotp && passwordLockerTotpDraft === null" class="totp-configured">
              <span class="hint-text">{{ t('totpConfiguredHint', props.lang) }}</span>
              <button class="button button--secondary button--tiny" @click="removePasswordLockerTotpDraft" type="button">{{ t('totpRemoveButton', props.lang) }}</button>
            </div>

            <!-- 狀態 2：使用者按了「移除」，還沒真的存檔——給一個反悔的機會 -->
            <div v-else-if="passwordLockerTotpDraft && !passwordLockerTotpDraft.secret" class="totp-configured">
              <span class="hint-text hint-text--danger">{{ t('totpWillBeRemovedHint', props.lang) }}</span>
              <button class="button button--secondary button--tiny" @click="passwordLockerTotpDraft = null" type="button">{{ t('cancel', props.lang) }}</button>
            </div>

            <!-- 狀態 3：已經解析出一組（新的或掃描出來的）密鑰，存檔前先讓使用者肉眼確認 -->
            <div v-else-if="passwordLockerTotpDraft && passwordLockerTotpDraft.secret" class="totp-preview">
              <svg viewBox="0 0 36 36" class="totp-ring">
                <circle class="totp-ring__track" cx="18" cy="18" r="16" />
                <circle class="totp-ring__progress" cx="18" cy="18" r="16" :style="totpRingStyle(passwordLockerTotpDraft.period)" />
              </svg>
              <span class="totp-preview__code">{{ passwordLockerTotpPreviewCode || '------' }}</span>
              <button class="button button--secondary button--tiny" @click="removePasswordLockerTotpDraft" type="button">{{ t('totpRemoveButton', props.lang) }}</button>
            </div>

            <!-- 狀態 4：還沒設定過——兩種輸入路徑並列 -->
            <div v-else class="totp-setup">
              <label class="button button--secondary button--tiny totp-setup__upload">
                {{ t('totpUploadQrButton', props.lang) }}
                <input type="file" accept="image/*" @change="handlePasswordLockerTotpQrFile" hidden />
              </label>
              <input
                type="text"
                :placeholder="t('totpManualPlaceholder', props.lang)"
                class="text-input"
                @input="handlePasswordLockerTotpManualInput($event.target.value)"
              />
              <p v-if="passwordLockerTotpQrError" class="hint-text hint-text--danger">{{ passwordLockerTotpQrError }}</p>
            </div>
          </div>

          <div class="modal__footer">
            <button class="button button--secondary" @click="closePasswordLockerForm" type="button">{{ t('cancel', props.lang) }}</button>
            <button class="button button--primary" @click="submitPasswordLockerForm" type="button">{{ t('saveButton', props.lang) }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 「關聯到現有帳號」第一步：挑一筆既有的「網站」類別憑證。複用清單資料，不需要
         另外呼叫後端。只列「網站」類別——「已加密檔案」沒有瀏覽器情境，關聯網域對它
         沒有意義。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerPickerVisible" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('associatePickerTitle', props.lang) }}</h2>
          <div class="table-scroll" style="max-height: 320px;">
            <table class="table" style="min-width: 0;">
              <colgroup>
                <col style="width: 20%;" />
                <col style="width: 80%;" />
              </colgroup>
              <tbody>
                <tr
                  v-for="item in passwordLockerWebsiteItems"
                  :key="item.id"
                  class="table__row--clickable"
                  @click="selectPasswordLockerAssociateTarget(item)"
                >
                  <td><div class="cell-name" style="max-width: 100%;" :title="passwordLockerDisplayTitle(item)">{{ passwordLockerDisplayTitle(item) }}</div></td>
                  <td><div class="cell-name" style="max-width: 100%;" :title="item.username">{{ item.username }}</div></td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" @click="passwordLockerPickerVisible = false" type="button">{{ t('cancel', props.lang) }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 第二步：輸入要新增的網域，標題選填（覆蓋自動組合出來的顯示名稱）。 -->
    <Transition name="password-locker-modal">
      <div v-if="passwordLockerAssociateState" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('associateDomainTitle', props.lang, { title: passwordLockerDisplayTitle(passwordLockerAssociateState.item) }) }}</h2>
          <div class="field">
            <label class="field__label">{{ t('associateDomainLabel', props.lang) }}</label>
            <input
              v-model="passwordLockerAssociateState.domainInput"
              class="text-input"
              :placeholder="t('domainsPlaceholder', props.lang)"
              @keyup.enter="submitPasswordLockerAssociateDomain"
            />
          </div>
          <div class="field">
            <label class="field__label">{{ t('associateTitleOverrideLabel', props.lang) }}</label>
            <input
              v-model="passwordLockerAssociateState.titleInput"
              class="text-input"
              :placeholder="t('associateTitleOverridePlaceholder', props.lang)"
            />
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" @click="passwordLockerAssociateState = null" type="button">{{ t('cancel', props.lang) }}</button>
            <button class="button button--primary" @click="submitPasswordLockerAssociateDomain" type="button">{{ t('associateConfirmButton', props.lang) }}</button>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
/* 套件自帶預設 CSS 變數（帶 fallback 值），外層可以覆蓋——見 ADR-0004。
   FileLocker.Web 現有的 .theme-vault 等機制會從外層覆蓋這些變數；PasswordVault.Web
   （全新專案，沒有 FileLocker 那套現成主題 CSS）不提供也能看到合理的預設樣式。
   只有顏色相關的屬性走 var(--x, fallback)，圓角/陰影/字級等維持原本 App.vue 的字面值——
   跟這個檔案原本（尚未搬移主畫面前）就定下的慣例一致。 */
/* 這個元件刻意不自己加外層 padding——頁面留白是「host 版面」的責任，不是元件內容的責任。
   FileLocker.Web 的 .page 容器本來就有 padding: 2rem 2.5rem 3rem，元件這裡如果自己再加一層
   padding 會疊加成兩倍留白（密碼庫標題上方那段異常空白就是這樣來的）。PasswordVault.Web
   沒有這種 host 版面容器，改成由它自己的 App.vue 補一層對等的 padding（見該檔案）。 */
.password-locker-page {
  color: var(--color-text, #1a1a1a);
  font-family: var(--font-ui, system-ui, sans-serif);
  text-align: left;
}

/* 字級是 0.875rem，不是看起來「應該」對應的 1.375rem——這是刻意配合 App.vue 那邊修好的
   既有 bug 之後的結果：App.vue 全域有一條 .app h1 { font-size:inherit } 優先權比單純
   .page-title 高，一直悄悄蓋掉全部六個分頁標題原本寫的 1.375rem，六個分頁多年來實際顯示
   的都是繼承算出來的 0.875rem。這裡故意跟著用一樣的「實際顯示值」而不是「原始碼寫的
   值」，兩邊才會長得一樣（見 App.vue 同一個 class 上的說明）。 */
.password-locker-page__title,
.page-title {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  font-size: 0.875rem;
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.2;
  margin: 0 0 1.75rem;
  color: var(--color-text, #1a1a1a);
  text-align: left;
}

/* SVG 本身沒有內建尺寸限制，不加寬高的話會撐到瀏覽器預設的 300x150 甚至更大——這裡固定
   跟文字同高（用 em 而不是 px，字級變動時圖示跟著縮放），flex-shrink:0 避免標題文字擠壓
   到圖示本身變形。 */
.page-title__icon {
  width: 1.35em;
  height: 1.35em;
  flex-shrink: 0;
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
  padding: 1.5rem;
  z-index: 1000;
}

.modal {
  background: var(--color-surface, #fff);
  border-radius: 16px;
  padding: 1.75rem;
  width: min(360px, 90vw);
  max-height: calc(100vh - 3rem);
  overflow-y: auto;
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.25);
  text-align: left;
}

/* 新增/編輯表單欄位比較多（分類／標題／關聯網站／帳號／密碼／備註／TOTP），
   沿用簡化版密碼提示彈窗的 360px 寬度會讓每個欄位都擠成好幾行，這裡放寬到
   跟 App.vue 原本的 .modal 一致（480px）。 */
.modal--form {
  width: min(480px, 90vw);
}

.modal__title {
  font-size: 1.15rem;
  font-weight: 700;
  margin: 0 0 0.5rem;
  color: var(--color-text, #1a1a1a);
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

.text-input,
.select-input {
  flex: 1;
  width: 100%;
  font-family: inherit;
  font-size: 0.95rem;
  color: var(--color-text, #1a1a1a);
  background: var(--color-surface, #fff);
  padding: 0.55rem 2.5rem 0.55rem 0.75rem;
  border-radius: 8px;
  border: 1px solid var(--color-border, #ccc);
}

.select-input {
  width: auto;
  min-width: 180px;
  padding-right: 0.75rem;
}

.select-input--compact {
  min-width: 0;
}

.text-input--mono {
  font-family: var(--font-mono, 'Consolas', 'Cascadia Code', monospace);
  font-size: 0.85rem;
}

textarea.text-input {
  resize: vertical;
  padding-right: 0.75rem;
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

/* 清單裡「顯示/隱藏」按鈕跟輸入欄裡的眼睛圖示共用同一顆元件，但這裡不是疊在輸入欄
   右側，改用一般文件流定位。 */
.password-field__toggle--inline {
  position: static;
  right: auto;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
}

.field {
  margin-bottom: 1.1rem;
}

.field__label {
  display: block;
  font-size: 0.825rem;
  font-weight: 500;
  color: var(--color-text-secondary, #666);
  margin-bottom: 0.4rem;
}

.checkbox-field {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  font-size: 0.875rem;
  color: var(--color-text, #1a1a1a);
  cursor: pointer;
  line-height: 1.5;
}

.hint-text {
  font-size: 0.8rem;
  line-height: 1.6;
  color: var(--color-text-secondary, #666);
  margin: 0.4rem 0 0;
}

.hint-text--danger {
  color: var(--color-danger, #b14328);
}

.text-warning-soft {
  color: var(--color-danger, #b14328);
  opacity: 0.85;
  font-weight: 600;
}

.button {
  /* App.vue 真正的 .button 是用 inline-flex + align-items:center 置中文字，不是單純
     padding——這才是文字上下留白不對稱（實測上方 18px、下方 16px）的真正原因：瀏覽器對
     inline/block 元素內文字的置中方式本來就會被字體 ascent/descent 影響，flex 置中才是
     不管字體都準的做法。同時這樣按鈕高度才會跟 .text-input／.select-input 這些用同一種
     置中邏輯的欄位對齊。 */
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  padding: 0.55rem 1rem;
  border-radius: var(--radius-sm, 6px);
  border: 1px solid transparent;
  font-size: 0.875rem;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
  transition: background var(--duration-fast, 150ms) var(--ease-out, ease-out),
    border-color var(--duration-fast, 150ms) var(--ease-out, ease-out),
    opacity var(--duration-fast, 150ms) var(--ease-out, ease-out),
    transform var(--duration-fast, 150ms) var(--ease-out, ease-out);
}

.button:active {
  transform: scale(0.97);
}

.button--primary {
  background: var(--color-accent, #a37e2c);
  color: #fff;
  box-shadow: var(--shadow-xs, none);
}

.button--primary:hover {
  background: var(--color-accent-hover, #8c630c);
}

.button--primary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.button--secondary {
  background: var(--color-surface, #fff);
  color: var(--color-text, #1a1a1a);
  border-color: var(--color-border-strong, #ccc);
}

.button--secondary:hover {
  border-color: var(--color-accent, #a37e2c);
  color: var(--color-accent, #a37e2c);
}

.button--danger {
  background: var(--color-danger, #b14328);
  color: #fff;
}

.button--danger:hover {
  background: #96351f;
}

.button--tiny {
  font-size: 0.76rem;
  padding: 0.28rem 0.5rem;
  background: var(--color-surface, #fff);
  color: var(--color-text-secondary, #666);
  border-color: var(--color-border, #ccc);
}

.button--tiny:hover {
  border-color: var(--color-accent, #a37e2c);
  color: var(--color-accent, #a37e2c);
}

/* 密碼庫內部小分頁（帳密清單／設定）——照抄 App.vue 已加密清單分頁既有的 .sub-tab-bar
   藥丸樣式，不新造一套，維持跟其他分頁一致的內部分頁視覺語言。 */
.sub-tab-bar {
  display: flex;
  gap: 0.5rem;
  margin-top: 1.25rem;
  margin-bottom: 1.25rem;
}

.sub-tab-bar__item {
  appearance: none;
  font-family: inherit;
  font-size: 0.82rem;
  font-weight: 500;
  /* 照抄 App.vue 既有 .sub-tab-bar__item 的既有修正：line-height:1 讓藥丸按鈕文字上下
     留白對稱，不然瀏覽器預設 line-height 會讓文字看起來偏上（實測上方比下方多 2px）。 */
  line-height: 1;
  border: 1px solid var(--color-border-strong, #ccc);
  background: var(--color-surface, #fff);
  color: var(--color-text-secondary, #666);
  border-radius: 999px;
  padding: calc(0.35rem + 2px) 0.85rem;
  cursor: pointer;
}

.sub-tab-bar__item.is-active {
  background: var(--color-accent-soft, #f5e9d3);
  border-color: var(--color-accent-border, #e4c77e);
  color: var(--color-accent, #a37e2c);
}

.link-button {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.8rem;
  color: var(--color-text-secondary, #666);
  cursor: pointer;
  padding: 0.2rem 0.4rem;
  text-decoration: underline;
}

.button-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-top: 0.5rem;
}

/* 密碼庫工具列：按鈕數量會隨選取狀態切換，這一列固定不換行，寬度不夠就橫向捲動，
   避免換行行為隨按鈕數量變化而改變、連帶讓下面清單的位置跟著跳動。 */
.button-row--nowrap {
  flex-wrap: nowrap;
  overflow-x: auto;
}

.settings-section {
  margin-bottom: 1.75rem;
  padding-bottom: 1.75rem;
  border-bottom: 1px solid var(--color-border, #ccc);
  text-align: left;
}

.settings-section:last-of-type {
  margin-bottom: 0;
  padding-bottom: 0;
  border-bottom: none;
}

.settings-section__title {
  font-size: 1.08rem;
  font-weight: 700;
  margin: 0 0 0.65rem;
  color: var(--color-text, #1a1a1a);
}

/* 密碼／Passkey／恢復金鑰／CSV 各自獨立一塊，用分隔線隔開——三者是各自獨立的解鎖路徑，
   混在同一排按鈕裡容易讓人以為彼此有關聯或互相依賴。 */
.settings-subsection {
  padding-top: 0.9rem;
  margin-top: 0.9rem;
  border-top: 1px solid var(--color-border, #ccc);
}

.settings-subsection:first-child {
  padding-top: 0;
  margin-top: 0;
  border-top: none;
}

.settings-subsection__title {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--color-text-secondary, #666);
  margin: 0 0 0.5rem;
}

.empty-state-block {
  padding: 3rem 1rem;
  text-align: center;
}

.empty-state-block--module {
  margin-top: 10vh;
}

.empty-state-block--module .button {
  margin-top: 1.25rem;
}

.empty-state-block__icon {
  width: 28px;
  height: 28px;
  padding: 14px;
  box-sizing: content-box;
  border-radius: 999px;
  background: var(--color-bg, #eee);
  color: var(--color-accent, #a37e2c);
  margin-bottom: 0.85rem;
}

.empty-state-block__icon--danger {
  background: var(--color-danger-soft, #fbe6e0);
  color: var(--color-danger, #b14328);
}

.empty-state-block__text {
  font-size: 0.85rem;
  color: var(--color-text-secondary, #666);
  margin: 0;
}

.table-scroll {
  overflow-x: auto;
  border-radius: var(--radius-md, 10px);
}

.table {
  width: 100%;
  min-width: 560px;
  border-collapse: collapse;
  font-size: 0.85rem;
  background: var(--color-surface, #fff);
}

.table th {
  text-align: left;
  font-weight: 500;
  color: var(--color-text-tertiary, #666);
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  padding: 0.65rem 0.85rem;
  border-bottom: 1px solid var(--color-border, #ccc);
}

.table td {
  padding: 0.7rem 0.85rem;
  border-bottom: 1px solid var(--color-border, #ccc);
  vertical-align: middle;
  white-space: nowrap;
}

.table tbody tr:last-child td {
  border-bottom: none;
}

.table tbody tr:hover td {
  background: var(--color-bg, #f5f5f5);
}

.table__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 0.3rem;
}

/* .button--tiny 的 0.76rem 字級是給別處（產生密碼／TOTP 表單裡的次要按鈕）用的，這裡
   （清單每一列的複製/編輯/刪除）改成跟同一列標題/帳號文字一樣的 0.85rem，只在這裡覆寫，
   不改 .button--tiny 本身，避免牽動其他用到它的地方。
   line-height:1 是額外补的——字級調大後，瀏覽器預設行高（中文字體的度量方式本來就跟
   純拉丁字母不一樣，保留的字符上下空間比例不對稱）讓文字看起來往框框下方偏，跟
   .sub-tab-bar__item 之前踩過的同一類問題（那邊是偏上，這裡是偏下，方向不同但成因一樣：
   預設行高不等於視覺置中），固定成 1 之後交給 align-items:center 用純幾何置中，不受
   字體行高摻進來的影響。 */
.table__actions .button--tiny {
  font-size: 0.85rem;
  line-height: 1;
  /* 使用者截圖比對過：修好置中之後，上方留白實測 6px、下方 7px——這裡不是重新猜一個
     對稱值，而是直接在現有留白上分別加碼（上 +3px、下 +2px，兩者相加後都會是 9px），
     跟原本 .button--tiny 共用的 0.28rem 垂直 padding 疊加，不影響其他用到 .button--tiny
     的地方。 */
  padding-top: calc(0.28rem + 3px);
  padding-bottom: calc(0.28rem + 2px);
}

.cell-name {
  font-weight: 500;
  max-width: 280px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
}

/* 清單裡「點一下就複製／顯示」的帳號欄位——蓋掉 .cell-name 的 cursor: default，
   讓使用者看得出這裡可以點，不用另外加圖示。 */
.cell-clickable {
  cursor: pointer;
}

/* 「已加密檔案」類別憑證對應的 Vault 項目消失後，標題加刪除線。 */
.text-strikethrough {
  text-decoration: line-through;
  opacity: 0.7;
}

.cell-empty {
  color: var(--color-text-secondary, #666);
}

.table__row--clickable {
  cursor: pointer;
}

.table__row--clickable:hover {
  background: var(--color-bg, #f5f5f5);
}

/* 表單裡的關聯網域標籤。 */
.tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--color-surface, #fff);
  color: var(--color-accent, #a37e2c);
  font-size: 0.8rem;
}

.tag__remove {
  appearance: none;
  border: none;
  background: none;
  color: inherit;
  cursor: pointer;
  font-size: 0.9rem;
  line-height: 1;
  padding: 0;
}

/* ---- TOTP 動態驗證碼：Google Authenticator 風格的圓形倒數，SVG stroke-dasharray／
   stroke-dashoffset 畫圓環，見 totpRingStyle() 的計算邏輯（totp.js 的 totpRingOffset）。
   track 是底色的完整圓、progress 疊在上面隨時間縮短，兩者共用同一個 stroke-dasharray
   （周長），只有 progress 的 dashoffset 會變。 ---- */
.totp-ring {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  transform: rotate(-90deg); /* 讓圓環從正上方開始縮短，而不是從三點鐘方向 */
}

.totp-ring__track {
  fill: none;
  stroke: var(--color-border, #ccc);
  stroke-width: 3;
}

.totp-ring__progress {
  fill: none;
  stroke-width: 3;
  stroke-linecap: round;
  /* 刻意比 tick 間隔（1s）短很多——每格移動要在下一次 tick 之前就已經動完、停穩，看起來
     是「一格一格接著動」，不是連續即時倒數那種整秒平滑掃過去的效果（那個效果會需要
     transition 時間跟 tick 間隔幾乎相等才做得到，兩者是不同的視覺設計，這裡選前者）。
     280ms ease-out 實測感覺軟趴趴、不夠乾脆——縮短到 150ms，曲線換成前段快、尾段更快收斂
     的 expo-out（cubic-bezier(0.16,1,0.3,1)），比對稱的 ease-out 更有「喀」一聲到位的
     俐落感，適合這種鐘錶指針式的單格跳動，不是需要溫和收尾的一般 UI 過場動畫。 */
  transition: stroke-dashoffset 150ms cubic-bezier(0.16, 1, 0.3, 1), stroke 200ms ease-out;
}

.totp-cell {
  display: flex;
  align-items: center;
  gap: 8px;
}

.totp-cell .password-field__toggle--inline {
  margin-right: 6px;
}

.totp-cell__code {
  font-size: 0.95rem;
  letter-spacing: 0.05em;
  cursor: pointer;
}

.totp-configured {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.totp-preview {
  display: flex;
  align-items: center;
  gap: 10px;
}

.totp-preview .totp-ring {
  width: 28px;
  height: 28px;
}

.totp-preview__code {
  font-family: var(--font-mono, 'Consolas', 'Cascadia Code', monospace);
  font-size: 1.1rem;
  letter-spacing: 0.08em;
  font-weight: 600;
}

.totp-setup {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.totp-setup__upload {
  align-self: flex-start;
  cursor: pointer;
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
