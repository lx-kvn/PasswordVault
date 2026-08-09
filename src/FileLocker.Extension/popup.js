// popup.js — 「選擇密碼」（Choose Password，見 CONTEXT.md）：偵測不到目前網站對應的既有憑證時，
// 讓使用者從密碼庫已存的網站帳密裡挑一筆重複使用。跟 App 內「關聯到現有帳號」是同一個底層機制
// （併入既有紀錄的 AssociatedDomains，不建立新紀錄），只是這裡走 native messaging 而不是 WebView2。
// 清單最下面的「新增密碼」是完全獨立的另一條路徑：直接在擴充功能裡建一筆全新的網站帳密，
// 不用切去 FileLocker 主視窗才能操作。

const listView = document.getElementById('listView')
const addView = document.getElementById('addView')
const listEl = document.getElementById('list')
const emptyEl = document.getElementById('empty')
const addButton = document.getElementById('addButton')
const manageButton = document.getElementById('manageButton')
const settingsButton = document.getElementById('settingsButton')
const statusEl = document.getElementById('status')

const addTitleInput = document.getElementById('addTitle')
const addUsernameInput = document.getElementById('addUsername')
const addPasswordInput = document.getElementById('addPassword')
const generateButton = document.getElementById('generateButton')
const addSubmitButton = document.getElementById('addSubmitButton')
const addCancelButton = document.getElementById('addCancelButton')

// popup 開啟時非同步載入一次（見檔案最下面的立即執行初始化），載入完成前的極短暫窗口內
// 用 zh-TW 當預設值——popup 每次都是使用者主動點開才會執行到會用到這個變數的程式碼，
// chrome.storage.local 的讀取速度遠快於使用者點擊到看見畫面內容的時間，實務上不是風險。
let currentLang = 'zh-TW'

function setStatus(message) {
  statusEl.hidden = !message
  statusEl.textContent = message || ''
}

function displayTitle(item) {
  if (item.title) return item.title
  if (item.associatedDomains?.length) return item.associatedDomains.join('、')
  return t('unnamed', currentLang)
}

async function getCurrentTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true })
  return tab || null
}

async function getCurrentTabDomain() {
  const tab = await getCurrentTab()
  if (!tab?.url) return null
  try {
    return new URL(tab.url).hostname
  } catch {
    return null
  }
}

async function loadCredentials() {
  const response = await chrome.runtime.sendMessage({ type: 'listPasswordLocker' })

  // Native Messaging Host 連不上（FileLocker 沒開、Host 沒註冊、extension-id.txt 沒設定）時
  // background.js 會回傳 { type: 'error', message }——這種情況不該跟「密碼庫裡真的沒有網站帳密」
  // 顯示成一樣的空清單，不然使用者永遠不知道問題出在連線層，還是真的沒存過密碼。
  if (!response || response.type === 'error') {
    setStatus(t('connectionFailed', currentLang, { message: response?.message || t('cannotConnectFileLocker', currentLang) }))
    return
  }

  const items = (response.items || []).filter((item) => item.category === 'Website')

  if (items.length === 0) {
    emptyEl.hidden = false
    return
  }

  for (const item of items) {
    const li = document.createElement('li')
    const titleSpan = document.createElement('span')
    titleSpan.textContent = displayTitle(item)
    const domainsSpan = document.createElement('span')
    domainsSpan.className = 'domains'
    domainsSpan.textContent = item.associatedDomains?.[0] || ''
    li.append(titleSpan, domainsSpan)
    li.addEventListener('click', () => useCredential(item))
    listEl.append(li)
  }
}

async function useCredential(item) {
  const currentDomain = await getCurrentTabDomain()
  if (!currentDomain) {
    setStatus(t('cannotDetermineDomain', currentLang))
    return
  }

  // 用被選中那筆本來就關聯的網域驗證身份（每網站獨立計時的滑動視窗 session，見規劃文件
  // 第 3 節）——這筆密碼「屬於」它自己既有的網域，用那個網域觸發驗證是正確的行為，
  // 不是憑空冒出一個跟這筆紀錄無關的網域。
  const ownDomain = item.associatedDomains?.[0]
  if (!ownDomain) {
    setStatus(t('notAssociatedWithSite', currentLang))
    return
  }

  setStatus(t('verifying', currentLang))
  const revealResult = await chrome.runtime.sendMessage({
    type: 'revealPasswordLockerCredentialForSite',
    id: item.id,
    domain: ownDomain,
    // 給 App 端驗證視窗顯示用（見 content-script.js 的 pickExistingCredential 同一段說明），
    // 不影響拿密碼用的是哪個網域驗證。
    targetDomain: currentDomain
  })
  if (!revealResult?.success) {
    setStatus(t('verifyFailedOrCancelled', currentLang))
    return
  }

  setStatus(t('addingAssociation', currentLang))
  const mergedDomains = Array.from(new Set([...(item.associatedDomains || []), currentDomain]))
  // 空標題維持空著，讓 App 端依 AssociatedDomains 動態組出顯示名稱；已經是字面標題才手動
  // 把新網站的名稱接上去，不然標題會一直卡在最初那個網站——理由見 content-script.js 同一段。
  const newSiteName = (await getCurrentTabSiteName()) || deriveTitleFromDomain(currentDomain)
  const newTitle = item.title ? `${item.title}、${newSiteName}` : ''
  await chrome.runtime.sendMessage({
    type: 'addOrUpdatePasswordLockerCredential',
    id: item.id,
    domain: currentDomain, // 不是欄位本身要存的值，是給 PasswordLockerNativePipeServer 判斷
    // 「尚未驗證要不要自動跳驗證視窗重試」用的（見 App.xaml.cs 的
    // RequestBrowserVerificationAsync／HandleMessageAsync 的重試條件），部件那邊不會讀這個屬性。
    category: 'Website',
    title: newTitle,
    domains: mergedDomains,
    username: item.username || '',
    usernameHidden: item.usernameHidden || false,
    password: revealResult.password
  })

  setStatus(t('filling', currentLang))
  await fillCurrentTab(item.username || '', item.usernameHidden || false, revealResult.password)
  setStatus(t('filled', currentLang))
  setTimeout(() => window.close(), 800)
}

async function fillCurrentTab(username, usernameHidden, password) {
  const tab = await getCurrentTab()
  if (!tab?.id) return
  try {
    await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: fillFormInPage,
      args: [username, usernameHidden, password]
    })
  } catch (err) {
    // chrome://、edge://、Chrome 線上應用程式商店這類特殊頁面不允許注入 script，
    // executeScript 會直接 reject（"Cannot access a chrome:// URL"）——這不是意外狀況，
    // 使用者可能就是在這種頁面上點開擴充功能，密碼庫本身的資料已經同步完成，只是沒有
    // 表單可以填，明確告知就好，不當成未處理的錯誤讓整個 popup 崩掉。
    setStatus(t('autofillUnsupported', currentLang))
  }
}

// 在頁面情境（不是擴充功能情境）執行，透過 chrome.scripting.executeScript 注入——
// 跟 content-script.js 的 fillForm 邏輯故意保持一致，這裡不能直接 import 那個檔案
// （executeScript 的 func 序列化執行環境跟 content script 是分開的）。
function fillFormInPage(username, usernameHidden, password) {
  const passwordField = document.querySelector('input[type="password"]')
  if (passwordField) {
    passwordField.value = password
    passwordField.dispatchEvent(new Event('input', { bubbles: true }))
    passwordField.dispatchEvent(new Event('change', { bubbles: true }))
  }
  if (username && !usernameHidden) {
    const usernameField = document.querySelector(
      'input[autocomplete="username"], input[type="email"], input[type="text"][name*="user" i], input[type="text"]'
    )
    if (usernameField) {
      usernameField.value = username
      usernameField.dispatchEvent(new Event('input', { bubbles: true }))
      usernameField.dispatchEvent(new Event('change', { bubbles: true }))
    }
  }
}

// 在頁面情境執行，回傳網站自己宣告的品牌名稱（見 title-utils.js 的 readPageSiteName）——
// 跟 fillFormInPage 同樣的原因不能直接呼叫 popup.js 這邊已經載入的 readPageSiteName，
// chrome.scripting.executeScript 的 func 是在目標分頁的頁面情境序列化執行，不是這個
// popup 自己的執行環境，函式體要能夠獨立、不依賴外部閉包變數。
function readPageSiteNameInPage() {
  const ogSiteName = document.querySelector('meta[property="og:site_name"]')?.content?.trim()
  if (ogSiteName) return ogSiteName
  const appName = document.querySelector('meta[name="application-name"]')?.content?.trim()
  if (appName) return appName
  return null
}

async function getCurrentTabSiteName() {
  const tab = await getCurrentTab()
  if (!tab?.id) return null
  try {
    const [{ result } = {}] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: readPageSiteNameInPage
    })
    return result
  } catch {
    return null // chrome:// 這類拿不到頁面內容的分頁，安靜落到網域猜測。
  }
}

function showAddView() {
  listView.style.display = 'none'
  addView.classList.add('visible')
  setStatus('')
  addUsernameInput.value = ''
  addPasswordInput.value = ''
  addTitleInput.value = ''
  addTitleInput.focus()
  prefillAddTitle()
}

function showListView() {
  addView.classList.remove('visible')
  listView.style.display = ''
  setStatus('')
}

// 只設 placeholder（灰字提示），不直接寫進 .value——標題留空，密碼庫清單本來就會在畫面上
// 動態組出顯示名稱，而且會隨著之後「選擇密碼」關聯更多網站自動一起列出來（見 App.vue 的
// passwordLockerDisplayTitle）。這裡如果直接把猜到的名稱寫進 .value 存成字面上的標題，
// 之後從別的網站選這筆密碼重複使用、關聯進新網站時，標題會卡在第一次存的那個網站名稱，
// 不會反映新關聯進來的網站——這正是這裡曾經踩過的問題，改成只當 placeholder。
async function prefillAddTitle() {
  const tab = await getCurrentTab()
  if (!tab?.id || !tab.url) return

  const siteName = await getCurrentTabSiteName()

  let domain
  try {
    domain = new URL(tab.url).hostname
  } catch {
    return
  }
  addTitleInput.placeholder = siteName || deriveTitleFromDomain(domain)
}

async function submitAddCredential() {
  const currentDomain = await getCurrentTabDomain()
  if (!currentDomain) {
    setStatus(t('cannotDetermineDomain', currentLang))
    return
  }
  const password = addPasswordInput.value
  if (!password) {
    setStatus(t('enterOrGeneratePassword', currentLang))
    return
  }

  addSubmitButton.disabled = true
  setStatus(t('verifying', currentLang))

  // 使用者沒自己打字就保持空字串——不要在這裡才把 placeholder 猜到的名稱補回去存成
  // 字面上的標題，理由見 prefillAddTitle 上的說明；「網站」分類只要有填至少一個關聯網站
  // 就不強制要標題，後端本來就接受空字串。
  const title = addTitleInput.value.trim()
  const username = addUsernameInput.value

  const result = await chrome.runtime.sendMessage({
    type: 'addOrUpdatePasswordLockerCredential',
    domain: currentDomain, // 給 PasswordLockerNativePipeServer 判斷要不要自動跳驗證視窗重試用，
    // 部件本身不會讀這個屬性，實際存進去的網域欄位是下面的 domains。
    category: 'Website',
    title,
    domains: [currentDomain],
    username,
    usernameHidden: false,
    password
  })

  addSubmitButton.disabled = false

  if (!result?.success) {
    setStatus(result?.errorMessage || t('saveFailed', currentLang))
    return
  }

  setStatus(t('filling', currentLang))
  await fillCurrentTab(username, false, password)
  setStatus(t('saved', currentLang))
  setTimeout(() => window.close(), 800)
}

manageButton.addEventListener('click', async () => {
  // 使用者明確要叫出 FileLocker 主視窗——跟其他情境刻意不叫視窗的設計不衝突（見
  // PasswordLockerNativePipeServer.HandleMessageAsync 對這個訊息類型的特殊處理）。
  await chrome.runtime.sendMessage({ type: 'openPasswordLockerApp' })
  window.close()
})

settingsButton.addEventListener('click', () => {
  // 設定（語言、預設電子信箱）集中在獨立設定頁（options.html，chrome.runtime.openOptionsPage()
  // 是 Chrome 標準 API），不在 popup 裡重複一份——popup 本身空間小，之後設定項目變多也不用
  // 一直往 popup 塞。
  chrome.runtime.openOptionsPage()
  window.close()
})

addButton.addEventListener('click', showAddView)
addCancelButton.addEventListener('click', showListView)
addSubmitButton.addEventListener('click', () => submitAddCredential().catch((err) => setStatus(t('saveFailed', currentLang) + `：${err.message}`)))
// 跟 App.vue 的 generatePasswordLockerPasswordAction 用同一組參數＋同一種每 5 碼一組用
// "-" 分隔的格式（groupWithDashes）——之前這裡是各自兜的參數（includeSymbols: true，沒有
// 分組），跟密碼庫本身的產生器格式對不起來，includeSymbols: true 產生的符號裡有不少網站
// 表單根本不接受的字元（例如引號、角括號），改成跟 App 一致的 includeSymbols: false。
function groupWithDashes(raw, groupSize = 5) {
  const groups = []
  for (let i = 0; i < raw.length; i += groupSize) {
    groups.push(raw.slice(i, i + groupSize))
  }
  return groups.join('-')
}

generateButton.addEventListener('click', async () => {
  const result = await chrome.runtime.sendMessage({ type: 'generatePasswordLockerPassword', length: 20, includeSymbols: false })
  if (result?.password) {
    addPasswordInput.value = groupWithDashes(result.password)
  }
})

// popup 開啟時立即執行：先套用語言（applyTranslations 處理 data-i18n 靜態文字），才跑
// 原本的清單載入——currentLang 這個模組層變數同時供 setStatus(...)／displayTitle(...)
// 這些動態組字串的地方使用（見宣告處說明）。
;(async () => {
  currentLang = await getLanguage()
  applyTranslations(currentLang)
  await loadCredentials()
})().catch((err) => setStatus(t('loadFailed', currentLang, { message: err.message })))
