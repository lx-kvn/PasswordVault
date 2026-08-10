// background.js — 密碼庫瀏覽器擴充功能的背景 service worker（Manifest V3）。
// 唯一負責跟 Native Messaging Host 對話的地方——content script／popup 都透過
// chrome.runtime.sendMessage 把請求丟過來，這裡轉發、拿到回應再轉發回去，
// 本身不落地存任何密碼、也不做任何業務判斷（見 FileLocker_密碼庫_功能規劃.md 第 5 節）。

const NATIVE_HOST_NAME = 'com.filelocker.passwordlocker'

// MV3 的 service worker 沒有常駐的全域狀態，每次收到訊息時才視需要建立一次性連線——
// chrome.runtime.sendNativeMessage 內部本來就是「開連線→送一則→收一則回應→關閉」，
// 剛好對應 Native Host 進程本身「每次連線就是一個新進程」的生命週期（見
// FileLocker.PasswordLockerNativeHost），不需要自己額外管理一個長駐的 connectNative port。
// 驗證流程可能需要使用者在 FileLocker 視窗裡操作（規劃文件第 5 節），所以這裡不設短逾時，
// 让 native messaging 自己的機制處理。
function forwardToNativeHost(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, message, (response) => {
      if (chrome.runtime.lastError) {
        resolve({ type: 'error', message: chrome.runtime.lastError.message })
        return
      }
      resolve(response)
    })
  })
}

// 「提交表單時填的密碼要不要存進密碼庫」的暫存區——純粹是「使用者填了新密碼、表單送出、
// 換頁」這一連串動作發生在同一個分頁裡，但 content script 每次換頁都是全新的執行環境，
// 換頁前記下的東西過不了頁面邊界。這裡不用打去 Native Host／FileLocker.App（那邊不需要
// 知道「使用者還沒決定要不要存」這種暫時性 UI 狀態），純粹用 chrome.storage.session 存在
// 擴充功能自己這一側，用分頁 id 當 key，換頁後新的 content script 讀一次就丟掉（見
// handleTakePendingSaveOffer 的 remove）。
function pendingSaveOfferKey(tabId) {
  return `pendingSaveOffer:${tabId}`
}

// stashedAt 記了卻從沒被檢查過（2026-08-09 這輪稽核發現）：如果換頁後那個分頁剛好停在
// content script 跑不到的地方（PDF 檢視器、chrome:// 內建頁、下載開始的分頁），這筆明文
// 密碼會一直留在 chrome.storage.session 裡，直到使用者哪天在同一個分頁 id 底下瀏覽到
// 一般網頁才被消費、彈出一張關於「幾小時前那個網站」的儲存卡片，使用者這時候多半已經不記得
// 脈絡、容易誤按。超過這個時限就視為過期，直接丟棄不再提示。
const PENDING_OFFER_TTL_MS = 5 * 60 * 1000

async function handleStashPendingSaveOffer(message, sender) {
  const tabId = sender.tab?.id
  if (tabId == null || !message.password) return { success: false }
  await chrome.storage.session.set({
    [pendingSaveOfferKey(tabId)]: {
      domain: message.domain,
      username: message.username || '',
      password: message.password,
      displayName: message.displayName || '',
      stashedAt: Date.now()
    }
  })
  return { success: true }
}

async function handleTakePendingSaveOffer(sender) {
  const tabId = sender.tab?.id
  if (tabId == null) return { offer: null }
  const key = pendingSaveOfferKey(tabId)
  const stored = await chrome.storage.session.get(key)
  const offer = stored[key] || null
  if (offer) await chrome.storage.session.remove(key)
  if (offer && Date.now() - offer.stashedAt > PENDING_OFFER_TTL_MS) {
    return { offer: null }
  }
  return { offer }
}

// 「選了『選擇密碼』某一筆之後，要不要把目前這個網站併進它的關聯網站」的暫存區——
// 跟上面的存密碼暫存區同一個道理、同一個換頁邊界問題（見 content-script.js 的
// pickExistingCredential／beforeunload 監聽器上的說明：曾經用 setTimeout 輪詢
// location.href 判斷換頁，真的整頁換頁時那個 timer 會跟著舊頁面一起被殺掉，永遠不會觸發，
// 這是「選了密碼卻沒有真的存進關聯」這個回報的根因）。
function pendingAssociationKey(tabId) {
  return `pendingAssociation:${tabId}`
}

async function handleStashPendingAssociation(message, sender) {
  const tabId = sender.tab?.id
  if (tabId == null || !message.association) return { success: false }
  await chrome.storage.session.set({
    [pendingAssociationKey(tabId)]: { ...message.association, stashedAt: Date.now() }
  })
  return { success: true }
}

async function handleTakePendingAssociation(sender) {
  const tabId = sender.tab?.id
  if (tabId == null) return { association: null }
  const key = pendingAssociationKey(tabId)
  const stored = await chrome.storage.session.get(key)
  const association = stored[key] || null
  if (association) await chrome.storage.session.remove(key)
  // 跟 handleTakePendingSaveOffer 同一個道理：換頁邊界卡在 content script 跑不到的頁面，
  // 這筆待確認的關聯請求（連同它暫存的明文密碼，見 content-script.js 的 pendingAssociation）
  // 就會一直留著，直到分頁哪天恰好又切回一般網頁才被消費，見上方 PENDING_OFFER_TTL_MS 的說明。
  if (association && Date.now() - association.stashedAt > PENDING_OFFER_TTL_MS) {
    return { association: null }
  }
  return { association }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === 'stashPendingPasswordLockerSaveOffer') {
    handleStashPendingSaveOffer(message, sender).then(sendResponse)
    return true
  }
  if (message?.type === 'takePendingPasswordLockerSaveOffer') {
    handleTakePendingSaveOffer(sender).then(sendResponse)
    return true
  }
  if (message?.type === 'stashPendingPasswordLockerAssociation') {
    handleStashPendingAssociation(message, sender).then(sendResponse)
    return true
  }
  if (message?.type === 'takePendingPasswordLockerAssociation') {
    handleTakePendingAssociation(sender).then(sendResponse)
    return true
  }
  forwardToNativeHost(message).then(sendResponse)
  return true // 非同步回應，要保持訊息通道開著（MV3 onMessage 的既定寫法）
})
