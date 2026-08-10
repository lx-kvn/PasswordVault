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
    header: 'PasswordVault',
    choosePasswordTitle: '選擇密碼',
    choosePasswordSubtitle: '從密碼庫已存的帳密裡挑一筆重複使用',
    choosePasswordHint: '從密碼庫裡挑一筆重複使用，會詢問要不要把目前這個網站加進它的關聯網站。',
    verifying: '驗證中，請留意 PasswordVault 視窗…',
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

    connectionFailed: '連線失敗：{message}（請確認 PasswordVault 已開啟，且已完成 Native Messaging Host 設定，見擴充功能 README）',
    cannotConnectFileLocker: '無法連上 PasswordVault',
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

    optionsTitle: 'PasswordVault 設定',
    languageLabel: '語言',
    defaultEmailSectionTitle: '預設電子信箱',
    defaultEmailHint: '在偵測到的註冊表單裡，帳號／電子信箱欄位是空的時候，會建議直接選用這隻信箱。',
    fieldEmailLabel: '電子信箱',
    emailPlaceholder: 'you@example.com',

    hide: '隱藏',
    totpLabel: '雙重驗證（TOTP）',
    totpRemoveButton: '移除',
    totpManualPlaceholder: '或貼上密鑰／otpauth:// 連結',
    // popup 不支援 QR code 圖片上傳——Chrome 擴充功能的 popup 失焦就會自動關閉，跳出原生
    // 「開啟檔案」對話框的瞬間 popup 會被判定失焦、整個關掉，見 popup.html/popup.js 附近
    // 的說明，這是 Chromium 平台本身的限制，不是能在這個 popup 裡修好的 bug。
    totpQrUnavailableInPopupHint: '要用 QR Code 掃描設定，請到 PasswordVault App 內的頁面。',
    totpShowButton: '顯示動態驗證碼',
    totpNotConfigured: '這筆紀錄沒有設定動態驗證碼',
    totpRevealFailed: '無法取得動態驗證碼',
    useTotpCodeTitle: '使用動態驗證碼',
    useTotpCodeSubtitle: '自動填入目前的驗證碼'
  },
  en: {
    header: 'PasswordVault',
    choosePasswordTitle: 'Choose password',
    choosePasswordSubtitle: 'Reuse a credential already saved in the password locker',
    choosePasswordHint: 'Reuse a credential from your password locker. You’ll be asked whether to associate this site with it.',
    verifying: 'Verifying, watch for the PasswordVault window…',
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

    connectionFailed: 'Connection failed: {message} (make sure PasswordVault is running and the Native Messaging Host is set up — see the extension README)',
    cannotConnectFileLocker: 'Could not connect to PasswordVault',
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

    optionsTitle: 'PasswordVault Settings',
    languageLabel: 'Language',
    defaultEmailSectionTitle: 'Default email',
    defaultEmailHint: 'When a detected sign-up form has an empty account/email field, this address will be suggested.',
    fieldEmailLabel: 'Email',
    emailPlaceholder: 'you@example.com',

    hide: 'Hide',
    totpLabel: 'Two-factor authentication (TOTP)',
    totpRemoveButton: 'Remove',
    totpManualPlaceholder: 'Or paste a secret / otpauth:// link',
    totpQrUnavailableInPopupHint: 'To set this up by scanning a QR code, use the PasswordVault app instead.',
    totpShowButton: 'Show 2FA code',
    totpNotConfigured: 'This entry doesn’t have a 2FA code set up',
    totpRevealFailed: 'Couldn’t get the 2FA code',
    useTotpCodeTitle: 'Use 2FA code',
    useTotpCodeSubtitle: 'Fill in the current code'
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

// ---- TOTP（RFC 6238）動態驗證碼——跟 src/FileLocker.PasswordLocker/TotpGenerator.cs／
// TotpUriParser.cs 是同一套演算法各自實作一份（執行環境互相隔離，不能共享模組，跟這個檔案
// 其他函式的既有慣例一致）。C# 那份的單元測試已經對照 RFC 6238 官方測試向量驗證過正確性，
// 這裡的 Base32／HMAC 動態截斷邏輯逐行照抄同一套演算法，維持行為一致。 ----

const BASE32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'

/// RFC 4648 Base32 解碼，忽略大小寫、空白、'-' 分隔線與補零用的 '='——使用者從網站 2FA
/// 設定頁複製貼上的密鑰常見這些雜訊，不要求呼叫端先清理。
function base32Decode(base32) {
  const cleaned = base32.trim().replace(/[\s-]/g, '').replace(/=+$/, '').toUpperCase()
  const bytes = []
  let buffer = 0
  let bitsInBuffer = 0

  for (const char of cleaned) {
    const value = BASE32_ALPHABET.indexOf(char)
    if (value < 0) {
      throw new Error(`不是合法的 Base32 字元：'${char}'`)
    }
    buffer = (buffer << 5) | value
    bitsInBuffer += 5
    if (bitsInBuffer >= 8) {
      bitsInBuffer -= 8
      bytes.push((buffer >> bitsInBuffer) & 0xff)
    }
  }
  return new Uint8Array(bytes)
}

function totpHashAlgorithmName(algorithm) {
  switch ((algorithm || 'SHA1').toUpperCase()) {
    case 'SHA256': return 'SHA-256'
    case 'SHA512': return 'SHA-512'
    default: return 'SHA-1'
  }
}

/// Web Crypto API（crypto.subtle）是非同步的，跟 C# 那份的同步版本不同，呼叫端要記得 await。
/// secret 是 Base32 字串（使用者實際存的密鑰格式），不是原始位元組。
async function computeTotpCode(base32Secret, algorithm, digits, periodSeconds, nowMs = Date.now()) {
  const keyBytes = base32Decode(base32Secret)
  const counter = Math.floor(nowMs / 1000 / periodSeconds)

  // RFC 4226 要求 8-byte、big-endian 的計數器。
  const counterBytes = new Uint8Array(8)
  let remaining = BigInt(counter)
  for (let i = 7; i >= 0; i--) {
    counterBytes[i] = Number(remaining & 0xffn)
    remaining >>= 8n
  }

  const cryptoKey = await crypto.subtle.importKey(
    'raw', keyBytes, { name: 'HMAC', hash: { name: totpHashAlgorithmName(algorithm) } }, false, ['sign'])
  const hashBuffer = await crypto.subtle.sign('HMAC', cryptoKey, counterBytes)
  const hash = new Uint8Array(hashBuffer)

  // RFC 4226 動態截斷：雜湊最後一個位元組的低 4 位當偏移量，從該偏移量取 4 個位元組、
  // 最高位元清零組成 31-bit 整數。
  const offset = hash[hash.length - 1] & 0x0f
  const binaryCode = ((hash[offset] & 0x7f) << 24)
    | ((hash[offset + 1] & 0xff) << 16)
    | ((hash[offset + 2] & 0xff) << 8)
    | (hash[offset + 3] & 0xff)

  const otp = binaryCode % (10 ** digits)
  return String(otp).padStart(digits, '0')
}

/// 解析 otpauth://totp/{Issuer}:{account}?secret=...&issuer=...&algorithm=...&digits=...&period=...
/// ——見 TotpUriParser.cs 開頭的說明，這不是 RFC 標準，是 Google Authenticator 訂的事實標準
/// 格式。解不出來回傳 null，呼叫端自己決定要不要退回「當作裸 Base32 密鑰」處理。
function parseOtpAuthUri(uriString) {
  let parsed
  try {
    parsed = new URL(uriString.trim())
  } catch {
    return null
  }
  if (parsed.protocol !== 'otpauth:' || parsed.host.toLowerCase() !== 'totp') {
    return null
  }

  const secret = parsed.searchParams.get('secret')
  if (!secret) {
    return null
  }

  const algorithm = (parsed.searchParams.get('algorithm') || 'SHA1').toUpperCase()
  const digits = parseInt(parsed.searchParams.get('digits') || '6', 10)
  const period = parseInt(parsed.searchParams.get('period') || '30', 10)

  const label = decodeURIComponent(parsed.pathname.replace(/^\//, ''))
  const colonIndex = label.indexOf(':')
  const labelIssuer = colonIndex >= 0 ? label.slice(0, colonIndex) : null
  const accountLabel = colonIndex >= 0 ? label.slice(colonIndex + 1) : label
  const issuer = parsed.searchParams.get('issuer') || labelIssuer

  return { secret, algorithm, digits, period, issuer, accountLabel }
}

/// 手動輸入欄位的統一解析入口：先當 otpauth:// URI 試著解析，解不出來就當作裸 Base32 密鑰
/// （搭配標準預設值 SHA1/6/30）——多數網站的「無法掃描 QR code」備援選項給的就是裸密鑰，
/// 不是完整連結。
function parseTotpInput(text) {
  const trimmed = (text || '').trim()
  if (!trimmed) {
    return null
  }
  const parsed = parseOtpAuthUri(trimmed)
  if (parsed) {
    return parsed
  }
  return { secret: trimmed, algorithm: 'SHA1', digits: 6, period: 30, issuer: null, accountLabel: null }
}

/// 判斷手動輸入欄位「看起來打完了」，用來在使用者還打字的過程中就自動切到預覽畫面，不用
/// 等失焦或按 Enter——但不能對『任何解析得出東西的字串』都算完成，parseTotpInput 對裸
/// Base32 密鑰是來者不拒（單一個合法字元也會解析成功），如果拿 parseTotpInput 能不能解出
/// 東西當完成判斷，使用者打第一個字元畫面就會跳走，完全沒辦法繼續打。
/// - otpauth:// 連結：`new URL()` 成功解析＋帶著 secret 參數，這個結構本來就只有「完整
///   貼上」才湊得出來，逐字打幾乎不可能中途剛好符合，直接信任 parseOtpAuthUri 的結果。
/// - 裸 Base32 密鑰：額外要求長度至少 16 個字元（真實密鑰常見長度是 16／26／32，對應
///   10/16/20 bytes 的 Base32 編碼）——比這個門檻短的字串很可能還在輸入中，先不要跳走。
function isTotpInputComplete(text) {
  const trimmed = (text || '').trim()
  if (!trimmed) return false
  if (parseOtpAuthUri(trimmed)) return true
  const cleaned = trimmed.replace(/[\s-]/g, '').replace(/=+$/, '').toUpperCase()
  return /^[A-Z2-7]+$/.test(cleaned) && cleaned.length >= 16
}

// Google Authenticator 風格的圓形倒數——SVG <circle> 的 stroke-dasharray 固定用這個周長，
// stroke-dashoffset 隨剩餘比例即時計算，r=16 是 App.vue／popup.js 共用的既定半徑，兩邊的
// SVG 都要用同一個 r 值這個常數才有意義。
const TOTP_RING_CIRCUMFERENCE = 2 * Math.PI * 16

function totpSecondsRemaining(periodSeconds, nowMs = Date.now()) {
  const elapsed = (nowMs / 1000) % periodSeconds
  return periodSeconds - elapsed
}

function totpRingOffset(periodSeconds, nowMs = Date.now()) {
  const remainingRatio = totpSecondsRemaining(periodSeconds, nowMs) / periodSeconds
  return TOTP_RING_CIRCUMFERENCE * (1 - remainingRatio)
}
