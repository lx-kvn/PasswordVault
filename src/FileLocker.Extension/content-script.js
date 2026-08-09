// content-script.js — 偵測目前網頁有沒有已存憑證、有沒有登入表單，符合就在使用者聚焦帳號／
// 密碼欄位時，緊貼著那個欄位跳出一個像瀏覽器原生密碼建議（例如 Safari／iCloud 密碼）那樣的
// 下拉選單（FileLocker_密碼庫_功能規劃.md 第 5 節）。不採用「頁面載入就在畫面角落跳一張卡片」
// 的做法——那種固定位置的提示條使用者很容易忽略、也跟欄位本身的空間關係脫節，讀不出「這是在
// 講哪個欄位」；貼著使用者正在操作的欄位彈出，才符合密碼管理員的慣例心智模型。
// MVP 判斷式：頁面上有 input[type=password] 就當作「可能是登入頁」，不追求完美——規劃文件
// 第 5 節已經明確記錄「第一版連登入頁偵測準確度都還沒驗證過」是接受範圍內的既定認知。
//
// 網域比對不到已存憑證時（新網站、還沒存過），下拉選單改顯示「選擇密碼」——從密碼庫已存的
// 網站帳密裡挑一筆重複使用（跟 popup.js 的同名機制、App 內「關聯到現有帳號」是同一個底層
// 機制，見 CONTEXT.md）。選定並填入表單後，等頁面實際換頁（登入成功、送出表單後跳下一步）
// 才詢問要不要把目前網站併入那筆憑證的關聯網站——填完當下就問太早，使用者可能填完才發現
// 密碼打錯、根本還沒送出表單，問了也白問。

const DROPDOWN_ELEMENT_ID = 'filelocker-autofill-dropdown'
const ASSOCIATION_PROMPT_ELEMENT_ID = 'filelocker-associate-prompt'
const SAVE_OFFER_ELEMENT_ID = 'filelocker-save-offer-prompt'

// content script 一注入就非同步讀一次語言偏好（見 shared.js 的 getLanguage）——所有下拉
// 選單／確認卡片都是使用者聚焦欄位、送出表單這些互動之後才觸發，讀取 chrome.storage.local
// 這麼快的操作實務上一定早就完成了，跟 credentialsPromise 快取的接受度一致，不是新風險。
let currentLanguage = 'zh-TW'
getLanguage().then((lang) => { currentLanguage = lang })

let credentialsPromise = null // { domain, items } 的 Promise，同一頁只查一次、快取結果
let allWebsiteCredentialsPromise = null // 「選擇密碼」清單用，同一頁只查一次
let activeField = null
let repositionHandler = null
let usernameFilterHandler = null // { field, handler }，見 attachUsernameFilter
const attachedFields = new WeakSet() // 避免 MutationObserver 對同一個欄位重複掛監聽

// 「選擇密碼」選了一筆、填入表單之後，等頁面換頁才問要不要關聯——同一頁同時間只會有一組
// 待確認的關聯（使用者不可能同時透過兩個欄位分別選了兩筆密碼），不需要用陣列/佇列管理。
let pendingAssociation = null

// 記錄「這個密碼欄位目前的值，是我們自己透過已存憑證填進去的哪一個密碼」（見 fillForm）——
// 送出表單時用來判斷這組密碼是不是「使用者自己重新打了一組沒變過的舊密碼」，這種情況不用
// 問要不要存。故意不用「這個網域在密碼庫裡已經有帳號一樣的紀錄」當判斷依據（曾經這樣寫過，
// 已經證實是錯的）——密碼庫沒有把明文密碼交給頁面比對的管道，帳號一樣不代表密碼沒變，
// 使用者改密碼重新註冊／登入時帳號本來就會一樣，用帳號比對會把「密碼真的變了、正是該問
// 要不要覆蓋的情況」整個跳過，见 2026-08-09 這輪對話的回報。只有「這個值就是我們剛剛
// 自己填進去的」才是唯一可靠、不用問的訊號。
const filledPasswordValues = new WeakMap()

function findPasswordFields() {
  return Array.from(document.querySelectorAll('input[type="password"]'))
}

/// 「新設密碼」欄位（註冊、改密碼）判定：`autocomplete="new-password"` 是瀏覽器規範
/// 建議的標準標記，多數現代表單都有標；沒標的話退一步看 name/id 有沒有 new/register/
/// signup/confirm 這類字樣。跟「登入」欄位（要拿去比對已存憑證）是完全不同的兩種情境——
/// 這種欄位的正確行為是「提供產生新密碼」，不是「列出已存密碼給你選」，選了舊密碼填進
/// 這種欄位反而是錯的（等於新帳號沿用舊密碼）。
function isNewPasswordField(field) {
  const autocomplete = (field.getAttribute('autocomplete') || '').toLowerCase()
  if (autocomplete === 'new-password') return true
  const nameId = `${field.name || ''} ${field.id || ''}`.toLowerCase()
  return /new|register|signup|confirm/.test(nameId)
}

/// 「這個帳號／email 欄位屬於註冊表單」的判定：看它所在的 <form>（沒有表單包著就看整份
/// 文件）裡有沒有任何一個「新設密碼」欄位（見 isNewPasswordField）——註冊表單本來就會
/// 同時要求設一組新密碼，登入表單不會。判斷對了才建議使用預設電子信箱（見
/// renderDefaultEmailEntry），登入欄位不該被這個建議打斷。
function isRegistrationContext(field) {
  const scope = field.closest('form') || document
  return Array.from(scope.querySelectorAll('input[type="password"]')).some(isNewPasswordField)
}

/// 很多登入流程是「先輸入帳號按下一步，同一頁面才動態換成密碼欄位」（Google／Microsoft／
/// Spotify 都是這種分段式登入）——頁面上還沒出現密碼欄位的第一步，`input[type="text"]` 這種
/// 低信心度的萬用選擇器太容易誤中頁面上其他跟登入無關的文字欄位，所以只在「頁面上已經有密碼
/// 欄位」（判斷這確實是登入表單）時才放寬到那個萬用選擇器；密碼欄位還沒出現的那一步，只信任
/// `autocomplete="username"` 或 `type="email"` 這種高信心度標記。
/// root 預設整份文件——多數頁面只有一個登入表單，直接在整個文件裡找沒問題。但送出表單
/// 那一刻（見 maybeOfferToSaveSubmittedPassword）一定要把 root 限定成「剛剛送出的那個
/// <form>」，不能繼續用整份文件：像測試頁那種登入／註冊／忘記密碼三個表單都同時存在
/// DOM 裡、只用 CSS 切換顯示的頁面，`document.querySelector` 永遠只會抓到 DOM 順序最前面
/// 那個表單裡符合的欄位（例如永遠抓到登入表單裡空著的 email 欄位），不會是使用者實際
/// 填寫、送出的那個表單，導致記錄到的帳號是空的或錯的。
function findUsernameField(hasPasswordField, root = document) {
  // 逐一查詢、依序取第一個查得到的，不能把整串優先順序丟給單一次 querySelector(a, b, c)——
  // 逗號分隔的選擇器清單，querySelector 是照「DOM 順序」找第一個符合任一子選擇器的元素，
  // 不會照你寫的子選擇器順序當優先權。像測試頁註冊表單裡，「你的名字」欄位（僅符合最後那個
  // 萬用的 input[type="text"]）如果剛好排在 DOM 順序中 email 欄位前面，会先被抓到、蓋掉
  // 真正該抓的 autocomplete="username" 欄位——這個 bug 實際發生過（見 2026-08-09 這輪對話），
  // 逐一查詢才能確保信心度高的選擇器真的優先。
  const selectors = hasPasswordField
    ? ['input[autocomplete="username"]', 'input[type="email"]', 'input[type="text"][name*="user" i]', 'input[type="text"]']
    : ['input[autocomplete="username"]', 'input[type="email"]']
  for (const selector of selectors) {
    const field = root.querySelector(selector)
    if (field) return field
  }
  return null
}

/// 「顯示名稱」欄位（例如註冊表單常見的「你的名字」），跟登入用的帳號／email 是兩回事——
/// 密碼庫的「帳號」欄位語意上是登入時真正要打的識別碼，混進顯示名稱只是用來稱呼使用者的
/// 資訊，帳號欄位的用途會變得不清楚（這個 bug 也是同一輪對話發現的：findUsernameField
/// 的萬用選擇器把這種欄位誤認成帳號）。密碼庫本身不新增獨立欄位存這個（見對話決策），
/// 找得到的話改存進備註。
function findDisplayNameField(root = document) {
  const byAutocomplete = root.querySelector('input[autocomplete="name"]')
  if (byAutocomplete) return byAutocomplete
  return root.querySelector('input[type="text"][name*="name" i]:not([name*="user" i]), input[type="text"][id*="name" i]:not([id*="user" i])')
}

async function loadCredentialsForCurrentDomain() {
  if (credentialsPromise) return credentialsPromise
  const domain = window.location.hostname
  credentialsPromise = chrome.runtime.sendMessage({ type: 'findPasswordLockerCredentialsForDomain', domain }).then((response) => {
    if (!response || response.type === 'error') {
      // 下拉選單本身是錦上添花、查不到就安靜不出聲，但連線失敗跟「真的沒有已存憑證」是兩回事，
      // 至少留一行 console 訊息，不然開發者在頁面 DevTools 主控台完全看不出到底是「沒有比對到」
      // 還是「Native Messaging Host 根本連不上」。
      console.warn('[FileLocker] 查詢密碼庫失敗：', response?.message || '無回應（Native Messaging Host 未連線）')
      return { domain, items: [] }
    }
    return { domain, items: response.items || [] }
  })
  return credentialsPromise
}

/// 「選擇密碼」用：跟網域比對無關，列出密碼庫裡所有「網站」分類的憑證——不需要驗證身份
/// 才能查（見 listPasswordLocker 本來就不要求驗證，只是不含密碼明文）。
async function loadAllWebsiteCredentials() {
  if (allWebsiteCredentialsPromise) return allWebsiteCredentialsPromise
  allWebsiteCredentialsPromise = chrome.runtime.sendMessage({ type: 'listPasswordLocker' }).then((response) => {
    if (!response || response.type === 'error') {
      console.warn('[FileLocker] 查詢密碼庫清單失敗：', response?.message || '無回應（Native Messaging Host 未連線）')
      return []
    }
    return (response.items || []).filter((item) => item.category === 'Website')
  })
  return allWebsiteCredentialsPromise
}

function removeDropdown() {
  document.getElementById(DROPDOWN_ELEMENT_ID)?.remove()
  if (repositionHandler) {
    window.removeEventListener('scroll', repositionHandler, true)
    window.removeEventListener('resize', repositionHandler)
    repositionHandler = null
  }
  if (usernameFilterHandler) {
    usernameFilterHandler.field.removeEventListener('input', usernameFilterHandler.handler)
    usernameFilterHandler = null
  }
  activeField = null
}

/// 已經有已存憑證的網站，帳號欄位打字即時篩選選單——打的內容跟這個網域底下任何一筆
/// 已知帳號都對不上，就先把選單藏起來（不整個關掉、不清空 activeField，欄位還是聚焦著、
/// 使用者隨時可能改主意繼續刪字），清空欄位或改打回對得上的內容，選單原樣再出現。
/// 只是切換可見度，不重新建立/查詢 DOM——比對邏輯很輕量，不需要防抖。
function attachUsernameFilter(field, host, items) {
  const handler = () => {
    const typed = field.value.trim().toLowerCase()
    const hasMatch = !typed || items.some((item) => !item.usernameHidden && item.username && item.username.toLowerCase().includes(typed))
    host.style.display = hasMatch ? '' : 'none'
  }
  field.addEventListener('input', handler)
  usernameFilterHandler = { field, handler }
}

function positionHostToField(host, field) {
  const rect = field.getBoundingClientRect()
  host.style.top = `${rect.bottom + 6}px`
  host.style.left = `${rect.left}px`
  host.style.minWidth = `${Math.max(rect.width, 220)}px`
}

function buildDropdownHost(field) {
  const host = document.createElement('div')
  host.id = DROPDOWN_ELEMENT_ID
  host.style.all = 'initial'
  host.style.position = 'fixed'
  host.style.zIndex = '2147483647'
  positionHostToField(host, field)
  // closed shadow root：頁面自己的 JS 拿不到裡面的內容，這個下拉選單的存在跟內容不該被
  // 任意網頁腳本讀取或操縱（跟密碼庫的其他安全邊界一致）。
  const shadow = host.attachShadow({ mode: 'closed' })
  return { host, shadow }
}

const MENU_STYLE = `
  .menu {
    font-family: system-ui, -apple-system, sans-serif;
    background: #1f1f1f;
    color: #fff;
    border-radius: 10px;
    padding: 6px;
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
    border: 1px solid rgba(255, 255, 255, 0.08);
    display: flex;
    flex-direction: column;
    max-width: 320px;
    max-height: 320px;
    overflow-y: auto;
    transform-origin: top left;
    opacity: 0;
    transform: translateY(-4px) scale(0.98);
    transition: opacity 120ms ease-out, transform 120ms ease-out;
  }
  .menu[data-open] {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
  .header {
    font-size: 11px;
    color: #999;
    padding: 6px 10px 4px;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px;
    border-radius: 6px;
    cursor: pointer;
    border: none;
    background: transparent;
    color: #fff;
    font: inherit;
    text-align: left;
    width: 100%;
  }
  .item:hover, .item:focus-visible { background: #33342c; }
  .icon {
    width: 20px;
    height: 20px;
    border-radius: 5px;
    flex-shrink: 0;
    object-fit: contain;
  }
  .text { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
  .title { font-size: 13px; font-weight: 600; line-height: 1.3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .subtitle { font-size: 11px; color: #999; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .status { font-size: 12px; color: #d4a017; padding: 8px 10px; }
  .empty { font-size: 12px; color: #999; padding: 10px; text-align: center; }
`

/// 選單項目左邊的小圖示，統一用擴充功能自己的鎖頭圖示（跟工具列上的主圖示同一份），
/// 不要每個選單各自寫死一個「FL」字樣的色塊——那不是這個擴充功能的圖示，只是找不到
/// 圖示時湊出來的佔位符，容易讓使用者以為這個下拉選單跟 FileLocker 本體是兩個不相干
/// 的東西。圖示要能在任意網頁的頁面情境裡載入，manifest.json 的 web_accessible_resources
/// 要記得一起註冊 icons/48.png，不然瀏覽器會擋掉這個 chrome-extension:// 資源的載入。
function createIconElement() {
  const icon = document.createElement('img')
  icon.className = 'icon'
  icon.src = chrome.runtime.getURL('icons/48.png')
  icon.alt = ''
  return icon
}

/// 每個下拉選單項目共用的骨架（圖示＋標題＋副標題），呼叫端只需要接著掛自己的
/// click 行為、把 button 自己 append 進 menu——這裡不做這兩件事，因為不同選單項目
/// 對「點下去要做什麼」「什麼時候該把 button 加進 DOM」的需求差異太大，硬塞進共用
/// 函式反而增加閱讀負擔。
function createMenuItemButton(titleText, subtitleText) {
  const button = document.createElement('button')
  button.type = 'button'
  button.className = 'item'
  button.append(createIconElement())

  const text = document.createElement('div')
  text.className = 'text'
  const title = document.createElement('div')
  title.className = 'title'
  title.textContent = titleText
  const subtitle = document.createElement('div')
  subtitle.className = 'subtitle'
  subtitle.textContent = subtitleText
  text.append(title, subtitle)
  button.append(text)

  return { button, title, subtitle, text }
}

function renderDropdown(shadow, items, domain, field) {
  const style = document.createElement('style')
  style.textContent = MENU_STYLE

  const menu = document.createElement('div')
  menu.className = 'menu'
  shadow.append(style, menu)

  const header = document.createElement('div')
  header.className = 'header'
  header.textContent = t('header', currentLanguage)
  menu.append(header)

  for (const item of items) {
    const { button, title, text } = createMenuItemButton(
      item.title || item.associatedDomains?.[0] || domain,
      item.usernameHidden ? '••••••' : item.username || ''
    )
    menu.append(button)

    // mousedown 而不是 click：click 之前欄位會先觸發 blur，若這裡不搶先 preventDefault，
    // 欄位失焦會讓下拉選單在 click 事件送達前就被關掉。
    button.addEventListener('mousedown', (e) => e.preventDefault())
    button.addEventListener('click', async () => {
      text.replaceChildren()
      const statusText = document.createElement('div')
      statusText.className = 'subtitle'
      statusText.textContent = t('verifying', currentLanguage)
      text.append(title, statusText)

      const result = await chrome.runtime.sendMessage({
        type: 'revealPasswordLockerCredentialForSite',
        id: item.id,
        domain
      })
      removeDropdown()
      if (result?.success) {
        fillForm(item, result.password, field)
      }
    })
  }

  // 已經有已存憑證的網站，選單最下面還是要能選「選擇密碼」——使用者這次要登入的可能
  // 是這個網站的另一個帳號（密碼庫裡沒存過），不是清單裡列出來的那幾筆，見 2026-08-09
  // 這輪對話的回報。
  appendChoosePasswordItem(menu, header, field)

  requestAnimationFrame(() => menu.setAttribute('data-open', ''))
}

/// 「選擇密碼」這個選項本身：點下去把整個選單換成從密碼庫挑一筆重複使用的清單——
/// 不管是網域比對不到已存憑證時單獨顯示（見 renderChoosePasswordEntry），還是已經有
/// 已存憑證、附加在 renderDropdown 清單最下面，都是同一套行為，共用同一份實作。
function appendChoosePasswordItem(menu, header, field) {
  const { button } = createMenuItemButton(t('choosePasswordTitle', currentLanguage), t('choosePasswordSubtitle', currentLanguage))
  menu.append(button)

  button.addEventListener('mousedown', (e) => e.preventDefault())
  button.addEventListener('click', async () => {
    menu.replaceChildren(header)
    const loadingRow = document.createElement('div')
    loadingRow.className = 'status'
    loadingRow.textContent = t('loading', currentLanguage)
    menu.append(loadingRow)

    const items = await loadAllWebsiteCredentials()
    if (activeField !== field) return // 載入期間使用者已經切走或關掉選單

    menu.replaceChildren(header)
    if (items.length === 0) {
      const empty = document.createElement('div')
      empty.className = 'empty'
      empty.textContent = t('emptyWebsiteCredentials', currentLanguage)
      menu.append(empty)
      return
    }
    renderCredentialPickerItems(menu, items, field)
  })
}

/// 網域比對不到已存憑證時顯示——單一一行「選擇密碼」，點下去才真的去查完整清單（見
/// appendChoosePasswordItem），避免每個聚焦事件都白白多打一次 listPasswordLocker。
function renderChoosePasswordEntry(shadow, domain, field) {
  const style = document.createElement('style')
  style.textContent = MENU_STYLE

  const menu = document.createElement('div')
  menu.className = 'menu'
  shadow.append(style, menu)

  const header = document.createElement('div')
  header.className = 'header'
  header.textContent = t('header', currentLanguage)
  menu.append(header)

  appendChoosePasswordItem(menu, header, field)

  requestAnimationFrame(() => menu.setAttribute('data-open', ''))
}

/// 跟 popup.js 的 groupWithDashes 是同一份邏輯的複製——這裡不能直接 import 那個檔案
/// （content script 跟 popup 是分開的執行環境），維持同一組參數／格式（見 popup.js
/// 對這段的說明：includeSymbols: false，5 碼一組用 "-" 分隔），確保不管從哪個入口
/// 產生的密碼格式都一致。
function groupWithDashes(raw, groupSize = 5) {
  const groups = []
  for (let i = 0; i < raw.length; i += groupSize) {
    groups.push(raw.slice(i, i + groupSize))
  }
  return groups.join('-')
}

/// 新設密碼欄位聚焦時顯示——只有「使用建議密碼」這一個選項，點下去才呼叫產生器（跟
/// App 內設定頁、擴充功能「新增密碼」畫面用同一個後端訊息類型 generatePasswordLockerPassword，
/// 保證強度／格式一致）。
function renderGeneratePasswordEntry(shadow, field) {
  const style = document.createElement('style')
  style.textContent = MENU_STYLE

  const menu = document.createElement('div')
  menu.className = 'menu'
  shadow.append(style, menu)

  const header = document.createElement('div')
  header.className = 'header'
  header.textContent = t('header', currentLanguage)
  menu.append(header)

  const { button, title, text } = createMenuItemButton(t('generatePasswordTitle', currentLanguage), t('generatePasswordSubtitle', currentLanguage))
  menu.append(button)

  button.addEventListener('mousedown', (e) => e.preventDefault())
  button.addEventListener('click', async () => {
    text.replaceChildren()
    const statusText = document.createElement('div')
    statusText.className = 'subtitle'
    statusText.textContent = t('generating', currentLanguage)
    text.append(title, statusText)

    const result = await chrome.runtime.sendMessage({ type: 'generatePasswordLockerPassword', length: 20, includeSymbols: false })
    removeDropdown()
    if (!result?.password) return
    fillNewPasswordFields(field, groupWithDashes(result.password))
  })

  requestAnimationFrame(() => menu.setAttribute('data-open', ''))
}

/// 預設電子信箱存在 chrome.storage.local（不是密碼庫的加密資料，只是使用者自己輸入、
/// 用來省打字的信箱字串，不需要走 Native Messaging Host／FileLocker.App 的加密管線）。
/// content script／popup 都能直接呼叫，不用經過 background.js 轉發。
function getDefaultEmail() {
  return new Promise((resolve) => {
    chrome.storage.local.get('defaultEmail', (result) => resolve(result.defaultEmail || ''))
  })
}

/// 註冊表單的帳號／email 欄位聚焦、還是空的時候顯示——只有「使用預設電子信箱」這一個
/// 選項，點下去直接把使用者在 popup 設定好的信箱填進去，跟 renderGeneratePasswordEntry
/// 是同一種「新設情境給單一建議」的模式。
function renderDefaultEmailEntry(shadow, field, defaultEmail) {
  const style = document.createElement('style')
  style.textContent = MENU_STYLE

  const menu = document.createElement('div')
  menu.className = 'menu'
  shadow.append(style, menu)

  const header = document.createElement('div')
  header.className = 'header'
  header.textContent = t('header', currentLanguage)
  menu.append(header)

  const { button } = createMenuItemButton(defaultEmail, t('defaultEmailSubtitle', currentLanguage))
  menu.append(button)

  button.addEventListener('mousedown', (e) => e.preventDefault())
  button.addEventListener('click', () => {
    field.value = defaultEmail
    field.dispatchEvent(new Event('input', { bubbles: true }))
    field.dispatchEvent(new Event('change', { bubbles: true }))
    removeDropdown()
  })

  requestAnimationFrame(() => menu.setAttribute('data-open', ''))
}

/// 產生的密碼同時填進頁面上所有「新設密碼」欄位——多數註冊表單有「密碼」＋「確認密碼」
/// 兩個欄位，只填使用者聚焦的那一個，另一個確認欄位還是空的，使用者送出表單時就會卡在
/// 「兩次密碼不一致」。找不到其他新設密碼欄位（只有一個密碼欄位的表單）就退回只填
/// 使用者聚焦的這一個。
function fillNewPasswordFields(focusedField, password) {
  const targets = findPasswordFields().filter(isNewPasswordField)
  for (const field of targets.length > 0 ? targets : [focusedField]) {
    field.value = password
    field.dispatchEvent(new Event('input', { bubbles: true }))
    field.dispatchEvent(new Event('change', { bubbles: true }))
  }
}

function renderCredentialPickerItems(menu, items, field) {
  for (const item of items) {
    const { button, title, text } = createMenuItemButton(
      item.title || item.associatedDomains?.[0] || t('unnamed', currentLanguage),
      item.associatedDomains?.[0] || ''
    )
    menu.append(button)

    button.addEventListener('mousedown', (e) => e.preventDefault())
    button.addEventListener('click', async () => {
      text.replaceChildren()
      const statusText = document.createElement('div')
      statusText.className = 'subtitle'
      statusText.textContent = t('verifying', currentLanguage)
      text.append(title, statusText)
      await pickExistingCredential(item, field)
    })
  }
}

/// 「選擇密碼」選定一筆之後：用那筆紀錄自己既有的網域觸發驗證（CONTEXT.md 的既定機制，
/// 這筆密碼「屬於」它自己既有的網域，不是憑空冒出一個跟這筆紀錄無關的網域），成功就填入
/// 表單，並記下這次待確認的關聯——真正詢問要不要關聯，等頁面換頁才問（見 pendingAssociation
/// 跟 stashPendingAssociationOnUnload 上的說明）。
async function pickExistingCredential(item, field) {
  const currentDomain = window.location.hostname
  const ownDomain = item.associatedDomains?.[0]

  if (!ownDomain) {
    removeDropdown()
    return
  }

  const result = await chrome.runtime.sendMessage({
    type: 'revealPasswordLockerCredentialForSite',
    id: item.id,
    domain: ownDomain,
    // 這筆密碼實際上會被填進 currentDomain（可能跟它自己歸屬的 ownDomain 不同）——
    // 附上這個欄位純粹是給 App 端跳出的驗證視窗顯示用（見 PasswordLockerBrowserVerifyWindow
    // 的雙網域說明），不影響拿密碼用的是哪個網域驗證。
    targetDomain: currentDomain
  })
  removeDropdown()
  if (!result?.success) {
    return
  }

  fillForm(item, result.password, field)

  if (!item.associatedDomains?.includes(currentDomain)) {
    pendingAssociation = { item, currentDomain, password: result.password }
  }
}

/// 頁面真的要卸載了（換頁／被關掉）才把還沒問過的關聯請求存過去——跟
/// maybeStashPasswordSaveOffer 是同一個道理：換頁前後是兩個完全獨立的 content script
/// 執行環境，換頁前用 setTimeout 輪詢 location.href 這種寫法（這裡曾經這樣寫過）在真的
/// 整頁換頁時必然失敗，因為輪詢用的 setTimeout 本身也活在舊頁面的 JS 環境裡，頁面一換
/// 那個 timer 連同它要偵測的東西一起被殺掉，永遠不會被觸發——這正是「選了密碼、有填進去，
/// 但關聯詢問卡片從來沒跳出來」的根因（見 2026-08-09 這輪對話的回報）。改用
/// chrome.storage.session（用分頁 id 當 key）撐過這個邊界，新頁面載入時再讀一次
/// （見 checkPendingAssociation）。用 beforeunload 而不是掛在 'submit' 事件上——有些登入
/// 流程按鈕點下去是用 JS 直接導頁、不一定會觸發表單 submit 事件，beforeunload 是「這個
/// 頁面真的要離開了」唯一可靠、涵蓋範圍最廣的訊號。
window.addEventListener('beforeunload', () => {
  if (!pendingAssociation) return
  chrome.runtime.sendMessage({
    type: 'stashPendingPasswordLockerAssociation',
    association: pendingAssociation
  }).catch(() => {})
})

/// 頁面換頁後才問（登入成功、表單送出後進到下一步，代表剛才填的密碼大概率是對的，
/// 這時候問「要不要關聯」才有意義；填完當下就問，使用者可能密碼打錯根本還沒送出表單）。
/// 用固定位置的卡片（不像下拉選單貼著特定欄位）——這時候通常已經看不到原本的欄位了
/// （頁面已經換過），沒有一個合理的錨點可以貼。
function showAssociationConfirm({ item, currentDomain, password }) {
  document.getElementById(ASSOCIATION_PROMPT_ELEMENT_ID)?.remove()

  const host = document.createElement('div')
  host.id = ASSOCIATION_PROMPT_ELEMENT_ID
  host.style.all = 'initial'
  host.style.position = 'fixed'
  // 貼著畫面正上方置中，不是右上角——右上角太容易被使用者忽略（尤其這張卡片是等頁面
  // 換頁後才冒出來，使用者這時候注意力通常在畫面中間新載入的內容上，不會主動往角落看）。
  host.style.top = '16px'
  host.style.left = '50%'
  host.style.transform = 'translateX(-50%)'
  host.style.zIndex = '2147483647'
  const shadow = host.attachShadow({ mode: 'closed' })

  const style = document.createElement('style')
  style.textContent = `
    .card {
      font-family: system-ui, -apple-system, sans-serif;
      background: #1f1f1f;
      color: #fff;
      border-radius: 10px;
      padding: 12px 14px;
      box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 220px;
      max-width: 280px;
    }
    .title { font-size: 13px; font-weight: 600; line-height: 1.4; }
    .row { display: flex; gap: 8px; }
    button {
      font: inherit;
      font-size: 12px;
      padding: 6px 10px;
      border-radius: 6px;
      border: none;
      cursor: pointer;
    }
    .primary { background: #d4a017; color: #1f1f1f; font-weight: 600; flex: 1; }
    .secondary { background: #3a3a3a; color: #fff; }
  `

  const card = document.createElement('div')
  card.className = 'card'
  const title = document.createElement('div')
  title.className = 'title'
  title.textContent = t('associationConfirmTitle', currentLanguage, {
    domain: currentDomain,
    title: item.title || item.associatedDomains?.[0] || ''
  })

  const row = document.createElement('div')
  row.className = 'row'
  const confirmButton = document.createElement('button')
  confirmButton.className = 'primary'
  confirmButton.type = 'button'
  confirmButton.textContent = t('associate', currentLanguage)
  const dismissButton = document.createElement('button')
  dismissButton.className = 'secondary'
  dismissButton.type = 'button'
  dismissButton.textContent = t('dismiss', currentLanguage)

  row.append(confirmButton, dismissButton)
  card.append(title, row)
  shadow.append(style, card)
  document.body.append(host)

  dismissButton.addEventListener('click', () => host.remove())
  confirmButton.addEventListener('click', async () => {
    confirmButton.disabled = true
    confirmButton.textContent = t('processing', currentLanguage)
    const mergedDomains = Array.from(new Set([...(item.associatedDomains || []), currentDomain]))
    // 這筆紀錄本來就是空標題（App 端會依 AssociatedDomains 動態組出顯示名稱，見
    // passwordLockerDisplayTitle）——維持空著，不用手動加，新網站併進 domains 之後清單自然
    // 就會反映出來。已經是字面標題（使用者自己取的名字，或這個部件更早之前存進去的）才需要
    // 手動把新網站的名稱接上去，不然這個字面標題會一直卡在最初那個網站，看不出後來關聯了
    // 誰——跟 App.vue 的 submitPasswordLockerAssociateDomain 是同一個道理，只是那邊是使用者
    // 自己打一個標籤，這裡沒有多一步輸入，改成自動用網站名稱當標籤。
    const newSiteName = readPageSiteName() || deriveTitleFromDomain(currentDomain)
    const newTitle = item.title ? `${item.title}、${newSiteName}` : ''
    await chrome.runtime.sendMessage({
      type: 'addOrUpdatePasswordLockerCredential',
      id: item.id,
      domain: currentDomain, // 給 PasswordLockerNativePipeServer 判斷要不要自動跳驗證視窗重試用，
      // 部件本身不會讀這個屬性，實際存進去的網域欄位是下面的 domains。
      category: 'Website',
      title: newTitle,
      domains: mergedDomains,
      username: item.username || '',
      usernameHidden: item.usernameHidden || false,
      password
    })
    host.remove()
  })
}

/// 使用者自己打了一組新密碼（不是走「選擇密碼」重複使用既有那筆）送出表單時，跟其他主流
/// 瀏覽器的密碼管理員一樣主動問要不要存——不問的話，密碼庫只能靠使用者自己記得回 App 手動
/// 補登，體驗上完全不像密碼管理員。跟「關聯既有網站」的道理一樣，等頁面真的換頁才問（見
/// pickExistingCredential 下面 beforeunload 監聽器上的說明：填完當下可能密碼打錯、表單根本
/// 沒送出，問了也白問）——但這裡的「換頁後才問」有一個換頁前後是兩個獨立 content script 執行環境的
/// 額外麻煩，用 background.js 的 chrome.storage.session（用分頁 id 當 key）把「待確認的
/// 存密碼請求」暫存過這個邊界，見 background.js 開頭的說明。
function maybeStashPasswordSaveOffer(domain, username, password, displayName) {
  chrome.runtime.sendMessage({
    type: 'stashPendingPasswordLockerSaveOffer',
    domain,
    username,
    password,
    displayName
  }).catch(() => {
    // 純粹是錦上添花的提示，暫存失敗（例如擴充功能正在重新載入）不該讓表單送出的
    // 主流程跟著出錯。
  })
}

/// 表單送出時的密碼，如果剛好就是我們自己剛剛透過已存憑證填進這個欄位的那個值、使用者
/// 沒有動過，才不用問（見 filledPasswordValues 宣告處的說明：這是唯一可靠的「密碼沒變」
/// 訊號）。其他情況——不管是這個網域在密碼庫裡根本還沒有紀錄、還是帳號一樣但密碼變了、
/// 甚至使用者自己手動重新打了一次已存的密碼——一律問，寧可偶爾多問一次使用者自己確認，
/// 也不要漏掉「密碼真的變了」這種真正該問的情況（覆蓋／另存新增由 showSaveOfferConfirm
/// 那邊依網域＋帳號查一次密碼庫決定，這裡不用先猜）。
function maybeOfferToSaveSubmittedPassword(passwordField, usernameField, displayNameField) {
  const password = passwordField.value
  if (!password) return
  if (filledPasswordValues.get(passwordField) === password) return

  const domain = window.location.hostname
  const username = usernameField?.value || ''
  const displayName = displayNameField?.value || ''

  maybeStashPasswordSaveOffer(domain, username, password, displayName)
}

/// 存密碼之前先查一次這個網域在密碼庫裡已經有哪些帳密——不查就直接存，同一個網站的
/// 第二個帳號、或密碼改過重新打一次，每次都會多出一筆新紀錄，而不是使用者實際期待的
/// 「覆蓋原本那筆」。回傳「應該覆蓋的那一筆」：帳號對得上（含兩邊都是空字串的情況）就是
/// 它；帳號對不上但這個網域剛好只有唯一一筆，也視為同一個帳號（多半是使用者這次沒填
/// 帳號欄位，或者網站帳號欄位沒被正確偵測到）；網域上有多筆又都對不上，覆蓋目標不明確，
/// 回傳 null，交給使用者自己選「另存新增」。
async function findOverwriteTargetForSaveOffer(domain, username) {
  const response = await chrome.runtime.sendMessage({ type: 'findPasswordLockerCredentialsForDomain', domain }).catch(() => null)
  const items = response?.items || []
  if (items.length === 0) return { items, target: null }
  const byUsername = items.find((item) => !item.usernameHidden && item.username === username)
  if (byUsername) return { items, target: byUsername }
  if (items.length === 1) return { items, target: items[0] }
  return { items, target: null }
}

async function saveCredential({ id, domain, username, password, displayName }) {
  await chrome.runtime.sendMessage({
    type: 'addOrUpdatePasswordLockerCredential',
    id, // 有給就更新既有那筆（覆蓋），不給（undefined）就新建一筆——跟 App.vue／popup.js
    // 既有的 add-vs-update 慣例一致。
    domain, // 給 PasswordLockerNativePipeServer 判斷要不要自動跳驗證視窗重試用，
    // 部件本身不會讀這個屬性，實際存進去的網域欄位是下面的 domains。
    category: 'Website',
    title: '', // 空標題留給 App 端依 AssociatedDomains 動態組出顯示名稱，理由跟
    // prefillAddTitle／showAssociationConfirm 同一段說明一致，這裡不重複貼一次 site 名稱。
    domains: [domain],
    username,
    usernameHidden: false,
    password,
    // 「顯示名稱」（例如註冊表單的「你的名字」）不新增獨立欄位存，直接放備註——但只有
    // id 是 undefined（新建這一筆）才帶上去。PasswordLockerService.AddOrUpdateCredentialAsync
    // 更新既有紀錄時是整欄覆蓋備註（見該方法），覆蓋既有那筆如果也塞這個，會把使用者原本
    // 在 App 裡自己寫的備註蓋掉。實務上「覆蓋」對應的情境本來就是既有帳號改密碼，登入／
    // 改密碼表單不會有「你的名字」這種欄位，這裡本來就不會有值，不算犧牲什麼。
    notes: id ? undefined : (displayName ? t('displayNameNotesLabel', currentLanguage, { name: displayName }) : undefined)
  })
}

/// 換頁後（見 maybeStashPasswordSaveOffer 上的說明）跳出來問要不要把上一頁送出的密碼
/// 存進密碼庫——版面跟 showAssociationConfirm 是同一套固定位置卡片樣式，這兩種提示不會
/// 同時出現（一個是「選了既有密碼」的情境，一個是「打了新密碼」的情境，彼此互斥）。
async function showSaveOfferConfirm({ domain, username, password, displayName }) {
  document.getElementById(SAVE_OFFER_ELEMENT_ID)?.remove()

  const { target: overwriteTarget } = await findOverwriteTargetForSaveOffer(domain, username)

  const host = document.createElement('div')
  host.id = SAVE_OFFER_ELEMENT_ID
  host.style.all = 'initial'
  host.style.position = 'fixed'
  // 貼著畫面正上方置中，不是右上角——右上角太容易被使用者忽略（尤其這張卡片是等頁面
  // 換頁後才冒出來，使用者這時候注意力通常在畫面中間新載入的內容上，不會主動往角落看）。
  host.style.top = '16px'
  host.style.left = '50%'
  host.style.transform = 'translateX(-50%)'
  host.style.zIndex = '2147483647'
  const shadow = host.attachShadow({ mode: 'closed' })

  const style = document.createElement('style')
  style.textContent = `
    .card {
      font-family: system-ui, -apple-system, sans-serif;
      background: #1f1f1f;
      color: #fff;
      border-radius: 10px;
      padding: 12px 14px;
      box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 220px;
      max-width: 280px;
    }
    .title { font-size: 13px; font-weight: 600; line-height: 1.4; }
    .row { display: flex; gap: 8px; }
    button {
      font: inherit;
      font-size: 12px;
      padding: 6px 10px;
      border-radius: 6px;
      border: none;
      cursor: pointer;
    }
    .primary { background: #d4a017; color: #1f1f1f; font-weight: 600; flex: 1; }
    .secondary { background: #3a3a3a; color: #fff; flex: 1; }
  `

  const card = document.createElement('div')
  card.className = 'card'
  const title = document.createElement('div')
  title.className = 'title'
  title.textContent = overwriteTarget
    ? t('saveOfferOverwriteTitle', currentLanguage, { domain })
    : t('saveOfferNewTitle', currentLanguage, { domain })

  const row = document.createElement('div')
  row.className = 'row'

  const dismissButton = document.createElement('button')
  dismissButton.className = 'secondary'
  dismissButton.type = 'button'
  dismissButton.textContent = t('dismiss', currentLanguage)
  dismissButton.addEventListener('click', () => host.remove())

  if (overwriteTarget) {
    const overwriteButton = document.createElement('button')
    overwriteButton.className = 'primary'
    overwriteButton.type = 'button'
    overwriteButton.textContent = t('overwrite', currentLanguage)
    overwriteButton.addEventListener('click', async () => {
      overwriteButton.disabled = true
      overwriteButton.textContent = t('processing', currentLanguage)
      await saveCredential({ id: overwriteTarget.id, domain, username, password, displayName })
      host.remove()
    })

    const saveAsNewButton = document.createElement('button')
    saveAsNewButton.className = 'secondary'
    saveAsNewButton.type = 'button'
    saveAsNewButton.textContent = t('saveAsNew', currentLanguage)
    saveAsNewButton.addEventListener('click', async () => {
      saveAsNewButton.disabled = true
      saveAsNewButton.textContent = t('processing', currentLanguage)
      await saveCredential({ domain, username, password, displayName })
      host.remove()
    })

    row.append(overwriteButton, saveAsNewButton, dismissButton)
  } else {
    const saveButton = document.createElement('button')
    saveButton.className = 'primary'
    saveButton.type = 'button'
    saveButton.textContent = t('save', currentLanguage)
    saveButton.addEventListener('click', async () => {
      saveButton.disabled = true
      saveButton.textContent = t('saving', currentLanguage)
      await saveCredential({ domain, username, password, displayName })
      host.remove()
    })

    row.append(saveButton, dismissButton)
  }

  card.append(title, row)
  shadow.append(style, card)
  document.body.append(host)
}

/// content script 剛載入（可能是換頁後全新的執行環境）就檢查一次有沒有上一頁留下來的
/// 待確認存密碼請求——見 maybeStashPasswordSaveOffer 上的換頁邊界說明。
async function checkPendingPasswordSaveOffer() {
  const response = await chrome.runtime.sendMessage({ type: 'takePendingPasswordLockerSaveOffer' }).catch(() => null)
  const offer = response?.offer
  if (offer?.domain && offer?.password) {
    showSaveOfferConfirm(offer)
  }
}

/// 跟 checkPendingPasswordSaveOffer 同一個道理，見 stashPendingAssociationOnUnload
/// （pickExistingCredential 下面那個 beforeunload 監聽器）上的說明。
async function checkPendingAssociation() {
  const response = await chrome.runtime.sendMessage({ type: 'takePendingPasswordLockerAssociation' }).catch(() => null)
  if (response?.association) {
    showAssociationConfirm(response.association)
  }
}

function fillForm(item, password, focusedField) {
  const passwordField = focusedField?.type === 'password' ? focusedField : (findPasswordFields()[0] ?? null)
  if (passwordField) {
    passwordField.value = password
    filledPasswordValues.set(passwordField, password) // 見宣告處說明：記下這是我們自己填的密碼
    passwordField.dispatchEvent(new Event('input', { bubbles: true }))
    passwordField.dispatchEvent(new Event('change', { bubbles: true }))
  }
  if (item.username && !item.usernameHidden) {
    const usernameField = focusedField?.type !== 'password' ? focusedField : findUsernameField(true)
    if (usernameField) {
      usernameField.value = item.username
      usernameField.dispatchEvent(new Event('input', { bubbles: true }))
      usernameField.dispatchEvent(new Event('change', { bubbles: true }))
    }
  }
}

async function showDropdownForField(field) {
  if (activeField === field) return
  removeDropdown()

  // 新設密碼欄位、而且使用者還沒自己打字進去——直接跳「使用建議密碼」，不查也不顯示
  // 已存憑證清單（見 isNewPasswordField 上的說明）。已經有內容的欄位代表使用者正在自己
  // 手動輸入，不要打斷。
  if (field.type === 'password' && isNewPasswordField(field) && !field.value) {
    activeField = field
    const { host, shadow } = buildDropdownHost(field)
    document.body.append(host)
    renderGeneratePasswordEntry(shadow, field)
    repositionHandler = () => positionHostToField(host, field)
    window.addEventListener('scroll', repositionHandler, true)
    window.addEventListener('resize', repositionHandler)
    return
  }

  // 註冊情境（同一個表單裡有新設密碼欄位）的帳號／email 欄位，欄位還是空的——優先建議
  // 使用者預先在 popup 設定好的預設信箱，不查已存憑證清單（這種情境本來就是要新開一個
  // 帳號，不是要重複使用舊帳號登入）。沒設定過預設信箱（空字串）就不進這個分支，往下
  // 走原本查已存憑證的邏輯。
  if (field.type !== 'password' && isRegistrationContext(field) && !field.value) {
    const defaultEmail = await getDefaultEmail()
    if (defaultEmail) {
      activeField = field
      const { host, shadow } = buildDropdownHost(field)
      document.body.append(host)
      renderDefaultEmailEntry(shadow, field, defaultEmail)
      repositionHandler = () => positionHostToField(host, field)
      window.addEventListener('scroll', repositionHandler, true)
      window.addEventListener('resize', repositionHandler)
      return
    }
  }

  const { items, domain } = await loadCredentialsForCurrentDomain()
  // 使用者可能在憑證查詢還沒回來之前就切到別的欄位，或欄位已經失焦——過期的回應不該
  // 憑空冒出一個貼著舊欄位（甚至已經不在畫面上）的下拉選單。
  if (document.activeElement !== field) return

  activeField = field
  const { host, shadow } = buildDropdownHost(field)
  document.body.append(host)

  if (items.length > 0) {
    renderDropdown(shadow, items, domain, field)
    // 帳號欄位（不是密碼欄位）才需要即時篩選——已知帳號跟使用者打的內容對不上就先藏起來，
    // 見 attachUsernameFilter 上的說明。
    if (field.type !== 'password') attachUsernameFilter(field, host, items)
  } else {
    // 這個網站在密碼庫裡還沒有已存憑證——不再直接什麼都不顯示，改提供「選擇密碼」，
    // 讓使用者能重複使用密碼庫裡其他已存的帳密（見檔案開頭說明）。
    renderChoosePasswordEntry(shadow, domain, field)
  }

  repositionHandler = () => positionHostToField(host, field)
  window.addEventListener('scroll', repositionHandler, true)
  window.addEventListener('resize', repositionHandler)
}

/// 頁面上如果同時有多個表單（例如登入／註冊／忘記密碼都在同一份文件裡，只是用 CSS
/// 切換顯示——見測試頁），每個表單各自找一個帳號欄位，不能只在整份文件裡找「一個」：
/// 那樣永遠只抓到 DOM 順序最前面那個表單的帳號欄位，其餘表單的帳號欄位完全不會被掛上
/// 偵測（這個 bug 讓註冊表單的 email 欄位聚焦完全沒反應，2026-08-09 這輪對話發現的）。
/// 沒有任何 <form> 包著欄位的頁面（欄位散在 body 底下）就退回整份文件搜尋。
function findAllUsernameFields(hasPasswordField) {
  const forms = document.querySelectorAll('form')
  if (forms.length === 0) {
    const field = findUsernameField(hasPasswordField)
    return field ? [field] : []
  }
  const fields = []
  for (const form of forms) {
    const field = findUsernameField(hasPasswordField, form)
    if (field && !fields.includes(field)) fields.push(field)
  }
  return fields
}

function attachFieldListeners() {
  const passwordFields = findPasswordFields()
  const fields = [...passwordFields, ...findAllUsernameFields(passwordFields.length > 0)]

  for (const field of fields) {
    if (attachedFields.has(field)) continue
    attachedFields.add(field)

    field.addEventListener('focus', () => {
      showDropdownForField(field).catch(() => {
        // 顯示下拉選單失敗（例如頁面 CSP 特別嚴格）不該讓整個頁面出現看得見的錯誤——
        // 這只是錦上添花的提示，使用者永遠可以改用擴充功能圖示的「選擇密碼」手動流程。
      })
    })
    // 點選選單項目時已經用 mousedown.preventDefault() 保住欄位焦點（見 renderDropdown），
    // 所以這裡收到 blur 一定是「真的離開這個欄位」（Tab 鍵切走、點別的地方），不是選單
    // 本身造成的——放心關掉選單，不用另外分辨來源。
    field.addEventListener('blur', () => {
      if (activeField === field) removeDropdown()
    })
  }
}

document.addEventListener('mousedown', (e) => {
  const dropdown = document.getElementById(DROPDOWN_ELEMENT_ID)
  if (!dropdown) return
  // 下拉選單掛在 closed shadow DOM 裡——從 document 這層看，選單內部元素觸發的事件會被
  // retarget 成外層 host（#filelocker-autofill-dropdown）本身，不是實際被點到的按鈕，
  // 所以「點到選單裡面」要比對 host，不能拿 e.target 去跟選單內部的東西比對。
  if (e.target !== activeField && e.target !== dropdown) {
    removeDropdown()
  }
})
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') removeDropdown()
})

// capture 階段監聽表單送出：頁面自己的 JS 常常會在 submit handler 裡呼叫
// preventDefault() 再用 fetch/XHR 送出（SPA 常見寫法），不管換不換頁，「使用者確認要送出
// 這組帳密」這個意圖在 submit 事件當下就已經成立，先在這裡把值讀出來、丟給
// maybeOfferToSaveSubmittedPassword 判斷要不要記下來（見該函式上的說明），不要等页面真的
// 卸載才處理（那時候欄位可能已經不在了）。
document.addEventListener('submit', (e) => {
  const form = e.target
  if (!(form instanceof HTMLFormElement)) return
  const passwordField = form.querySelector('input[type="password"]')
  if (!passwordField) return
  const usernameField = findUsernameField(true, form)
  const displayNameField = findDisplayNameField(form)
  maybeOfferToSaveSubmittedPassword(passwordField, usernameField, displayNameField)
}, true)

attachFieldListeners()
checkPendingPasswordSaveOffer()
checkPendingAssociation()

// 很多登入流程是分段式的單頁應用（先輸入帳號、按下一步後同一個頁面才動態換上密碼欄位，
// 不會觸發整頁重新載入），content script 只在頁面剛載入時跑一次的話，密碼欄位動態出現後
// 完全不會被偵測到。用 MutationObserver 監看整個文件的節點增減，欄位一旦新出現就補掛監聽
// ——用 attachedFields 這個 WeakSet 擋掉重複掛載，短時間內密集的 DOM 變動也不會疊加多次
// 監聽器。故意不加防抖／節流：attachFieldListeners 本身只是幾個 querySelector，成本很低，
// 不值得為了省這點成本另外引入排程邏輯的複雜度。
new MutationObserver(attachFieldListeners).observe(document.body, { childList: true, subtree: true })
