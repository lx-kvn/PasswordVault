// shared.js — content script、popup、options page 三個執行環境共用的工具函式，純 JS
// 不用 import/export（三邊都是用 <script src="shared.js"> 或 manifest.json 的
// content_scripts.js 陣列引入，跟 title-utils.js 同一種引入方式），全域函式／常數。
//
// 多語言：手動選單切換＋存進 chrome.storage.local，不是用 Chrome 擴充功能原生的
// chrome.i18n（那個是跟著瀏覽器/OS 語言自動判斷，沒有手動切換 UI）——跟 FileLocker.Web
// App.vue 的既有慣例一致（同一個產品裡兩邊的語言體驗要一致：使用者自己選、存偏好，
// 不受瀏覽器語言影響）。t(key, lang, params) 的介面直接照搬 App.vue 的 t()：找不到
// 對應語言檔案退回 zh-TW，再找不到就顯示 key 本身（方便開發時發現漏翻的字串），
// {name} 這種花括號佔位符用來塞動態內容。

const FL_LOCALES = {
  'zh-TW': {
    header: 'FileLocker 密碼庫',
    choosePasswordTitle: '選擇密碼',
    choosePasswordSubtitle: '從密碼庫已存的帳密裡挑一筆重複使用',
    choosePasswordHint: '從密碼庫裡挑一筆重複使用，會詢問要不要把目前這個網站加進它的關聯網站。',
    verifying: '驗證中，請留意 FileLocker 視窗…',
    loading: '載入中…',
    emptyWebsiteCredentials: '密碼庫裡還沒有網站帳密',
    generatePasswordTitle: '使用建議密碼',
    generatePasswordSubtitle: '產生一組高強度密碼並自動填入',
    generating: '產生中…',
    unnamed: '（未命名）',
    defaultEmailSubtitle: '使用預設電子信箱',
    associationConfirmTitle: '要把「{domain}」加進「{title}」的關聯網站嗎？',
    associate: '關聯',
    dismiss: '不用了',
    processing: '處理中…',
    saveOfferOverwriteTitle: '「{domain}」已經存過一組帳密，要覆蓋還是另外存成一筆？',
    saveOfferNewTitle: '要把「{domain}」的這組帳密存進密碼庫嗎？',
    overwrite: '覆蓋',
    saveAsNew: '另存新增',
    save: '儲存',
    saving: '儲存中…',

    addButtonLabel: '新增密碼',
    manageButtonLabel: '管理密碼',
    settingsButtonLabel: '設定',
    fieldTitleLabel: '標題',
    addTitlePlaceholder: '（留白會自動帶入網站名稱）',
    fieldUsernameLabel: '帳號',
    fieldPasswordLabel: '密碼',
    generateButtonLabel: '產生',
    cancelButtonLabel: '取消',

    connectionFailed: '連線失敗：{message}（請確認 FileLocker 已開啟，且已完成 Native Messaging Host 設定，見擴充功能 README）',
    cannotConnectFileLocker: '無法連上 FileLocker',
    cannotDetermineDomain: '無法判斷目前分頁的網域',
    notAssociatedWithSite: '這筆紀錄沒有關聯任何網站，無法重複使用',
    verifyFailedOrCancelled: '驗證失敗或已取消',
    addingAssociation: '正在把這個網站加進關聯清單…',
    filling: '正在填入…',
    filled: '已填入',
    autofillUnsupported: '這個頁面不支援自動填入，已儲存但沒有可以填入的表單',
    enterOrGeneratePassword: '請輸入或產生一組密碼',
    saveFailed: '儲存失敗',
    saved: '已儲存',
    loadFailed: '載入失敗：{message}',
    displayNameNotesLabel: '使用者名稱：{name}',

    optionsTitle: 'FileLocker 密碼庫設定',
    languageLabel: '語言',
    defaultEmailSectionTitle: '預設電子信箱',
    defaultEmailHint: '在偵測到的註冊表單裡，帳號／電子信箱欄位是空的時候，會建議直接選用這隻信箱。',
    fieldEmailLabel: '電子信箱',
    emailPlaceholder: 'you@example.com'
  },
  en: {
    header: 'FileLocker Password Locker',
    choosePasswordTitle: 'Choose password',
    choosePasswordSubtitle: 'Reuse a credential already saved in the password locker',
    choosePasswordHint: 'Reuse a credential from your password locker. You’ll be asked whether to associate this site with it.',
    verifying: 'Verifying, watch for the FileLocker window…',
    loading: 'Loading…',
    emptyWebsiteCredentials: 'No website credentials saved yet',
    generatePasswordTitle: 'Use a suggested password',
    generatePasswordSubtitle: 'Generate a strong password and fill it in',
    generating: 'Generating…',
    unnamed: '(Untitled)',
    defaultEmailSubtitle: 'Use default email',
    associationConfirmTitle: 'Add "{domain}" to the associated sites for "{title}"?',
    associate: 'Associate',
    dismiss: 'Not now',
    processing: 'Working…',
    saveOfferOverwriteTitle: '"{domain}" already has a saved credential. Overwrite it or save as a new one?',
    saveOfferNewTitle: 'Save this credential for "{domain}" to the password locker?',
    overwrite: 'Overwrite',
    saveAsNew: 'Save as new',
    save: 'Save',
    saving: 'Saving…',

    addButtonLabel: 'Add password',
    manageButtonLabel: 'Manage passwords',
    settingsButtonLabel: 'Settings',
    fieldTitleLabel: 'Title',
    addTitlePlaceholder: '(Leave blank to use the site name automatically)',
    fieldUsernameLabel: 'Username',
    fieldPasswordLabel: 'Password',
    generateButtonLabel: 'Generate',
    cancelButtonLabel: 'Cancel',

    connectionFailed: 'Connection failed: {message} (make sure FileLocker is running and the Native Messaging Host is set up — see the extension README)',
    cannotConnectFileLocker: 'Could not connect to FileLocker',
    cannotDetermineDomain: 'Could not determine the current tab’s domain',
    notAssociatedWithSite: 'This entry isn’t associated with any site, so it can’t be reused',
    verifyFailedOrCancelled: 'Verification failed or was cancelled',
    addingAssociation: 'Adding this site to the associated sites…',
    filling: 'Filling in…',
    filled: 'Filled in',
    autofillUnsupported: 'Autofill isn’t supported on this page. Saved, but there was no form to fill in.',
    enterOrGeneratePassword: 'Enter or generate a password',
    saveFailed: 'Save failed',
    saved: 'Saved',
    loadFailed: 'Load failed: {message}',
    displayNameNotesLabel: 'Name: {name}',

    optionsTitle: 'FileLocker Password Locker Settings',
    languageLabel: 'Language',
    defaultEmailSectionTitle: 'Default email',
    defaultEmailHint: 'When a detected sign-up form has an empty account/email field, this address will be suggested.',
    fieldEmailLabel: 'Email',
    emailPlaceholder: 'you@example.com'
  }
}

function t(key, lang, params) {
  let text = FL_LOCALES[lang]?.[key] ?? FL_LOCALES['zh-TW'][key] ?? key
  if (params) {
    for (const [paramKey, value] of Object.entries(params)) {
      text = text.replaceAll(`{${paramKey}}`, value)
    }
  }
  return text
}

function getLanguage() {
  return new Promise((resolve) => {
    chrome.storage.local.get('language', (result) => resolve(result.language || 'zh-TW'))
  })
}

function setLanguage(lang) {
  return new Promise((resolve) => {
    chrome.storage.local.set({ language: lang }, resolve)
  })
}

/// 預設電子信箱存在 chrome.storage.local（不是密碼庫的加密資料，只是使用者自己輸入、
/// 用來省打字的信箱字串，不需要走 Native Messaging Host／FileLocker.App 的加密管線）。
function getDefaultEmail() {
  return new Promise((resolve) => {
    chrome.storage.local.get('defaultEmail', (result) => resolve(result.defaultEmail || ''))
  })
}

function setDefaultEmail(value) {
  return new Promise((resolve) => {
    chrome.storage.local.set({ defaultEmail: value }, resolve)
  })
}

/// popup.html／options.html 的靜態文字改成標 data-i18n（textContent）／
/// data-i18n-placeholder（placeholder）屬性，不再寫死中文字面——這個函式負責套用，
/// 語言切換時（options 頁的下拉選單 onchange）重新呼叫一次就能整頁換語言，不用重新整理。
function applyTranslations(lang) {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    el.textContent = t(el.dataset.i18n, lang)
  })
  document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => {
    el.placeholder = t(el.dataset.i18nPlaceholder, lang)
  })
}
