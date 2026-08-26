// totp.js — TOTP（RFC 6238）動態驗證碼，App.vue 專用的一份實作。跟
// src/FileLocker.PasswordLocker/TotpGenerator.cs／TotpUriParser.cs、
// src/FileLocker.Extension/shared.js 是同一套演算法的第三份實作——三個執行環境互相隔離，
// 沒辦法共享模組（跟這個專案既有的 t()／groupWithDashes 重複多份的慣例一致）。C# 那份的
// 單元測試已經對照 RFC 6238 官方測試向量驗證過正確性，這裡逐行照抄同一套演算法。
//
// crypto.subtle 需要安全內容環境（HTTPS 或 localhost）——WebView2 開發模式載入
// http://localhost:5173（localhost 本身視為安全內容，跟協定無關），正式建置載入
// SetVirtualHostNameToFolderMapping 對應的 https://，兩種情況都滿足，不需要額外處理。

const BASE32_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'

/// RFC 4648 Base32 解碼，忽略大小寫、空白、'-' 分隔線與補零用的 '='。
export function base32Decode(base32) {
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

export async function computeTotpCode(base32Secret, algorithm, digits, periodSeconds, nowMs = Date.now()) {
  const keyBytes = base32Decode(base32Secret)
  const counter = Math.floor(nowMs / 1000 / periodSeconds)

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

  const offset = hash[hash.length - 1] & 0x0f
  const binaryCode = ((hash[offset] & 0x7f) << 24)
    | ((hash[offset + 1] & 0xff) << 16)
    | ((hash[offset + 2] & 0xff) << 8)
    | (hash[offset + 3] & 0xff)

  const otp = binaryCode % (10 ** digits)
  return String(otp).padStart(digits, '0')
}

export function parseOtpAuthUri(uriString) {
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
/// （搭配標準預設值 SHA1/6/30）。
export function parseTotpInput(text) {
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
export function isTotpInputComplete(text) {
  const trimmed = (text || '').trim()
  if (!trimmed) return false
  if (parseOtpAuthUri(trimmed)) return true
  const cleaned = trimmed.replace(/[\s-]/g, '').replace(/=+$/, '').toUpperCase()
  return /^[A-Z2-7]+$/.test(cleaned) && cleaned.length >= 16
}

// Google Authenticator 風格的圓形倒數——SVG <circle> 的 stroke-dasharray 固定用這個周長，
// r=16 是這個專案裡 TOTP 圓環固定用的半徑，跟 shared.js 那份保持一致（純粹是視覺常數，
// 沒有共用的必要，但數值要對得上避免兩邊圓環大小不一致）。
export const TOTP_RING_CIRCUMFERENCE = 2 * Math.PI * 16

export function totpSecondsRemaining(periodSeconds, nowMs = Date.now()) {
  const elapsed = (nowMs / 1000) % periodSeconds
  return periodSeconds - elapsed
}

export function totpRingOffset(periodSeconds, nowMs = Date.now()) {
  // 整個圓環切成 periodSeconds 個離散刻度（30 秒週期就是 30 格），一秒只移動一次刻度，
  // 不是跟著毫秒連續平滑縮短——四捨五入到整數秒；呼叫端的 tick 本身也是每秒才觸發一次，
  // 用連續小數比例算出來的位置在同一秒內其實看不出差異，但取整數能確保每次一定剛好移動
  // 一整格，搭配 CSS 的短暫 transition（見 .totp-ring__progress）才會是「一格一格接著動」
  // 而不是像即時倒數那樣連續平滑掃過去。
  const remainingWholeSeconds = Math.ceil(totpSecondsRemaining(periodSeconds, nowMs))
  const remainingRatio = remainingWholeSeconds / periodSeconds
  return TOTP_RING_CIRCUMFERENCE * (1 - remainingRatio)
}
