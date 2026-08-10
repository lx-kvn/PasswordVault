// title-utils.js — 新增密碼庫憑證時，標題留白要怎麼自動帶入預設值。純函式、不牽涉任何
// chrome.* API，content-script.js（在頁面情境，可以直接讀 DOM）跟 popup.js（透過
// chrome.scripting.executeScript 注入 readPageSiteName 到頁面情境執行）共用同一份邏輯。

// 常見的多段式 TLD——網域最後兩段本身就是「.com.tw」「.co.jp」這類固定尾綴時，真正代表
// 品牌的是尾綴前面那一段，不是「去掉最後一段」那麼單純（例：gamer.com.tw 去掉最後一段
// 是「gamer.com」，不是想要的「gamer」）。
const MULTI_PART_TLDS = ['com.tw', 'com.cn', 'com.hk', 'co.jp', 'co.uk', 'co.kr', 'com.au']

// 網域猜不出品牌名稱時的保底手段（例如 gamer.com.tw 對應「巴哈姆特」，網域完全看不出
// 關聯）——大部分網站會在 <head> 放 og:site_name 這個 Open Graph 標準 meta 標籤填自己的
// 品牌名稱，這是網站自己宣告的資料，比對網域字串做各種猜測準確得多，也不需要額外的
// 網路查詢（不發任何請求出去，純讀當前頁面已經載入好的 DOM）。application-name 是次選、
// 少數網站會用這個代替 og:site_name。都沒有的話，呼叫端要自己 fallback 到
// deriveTitleFromDomain。
function readPageSiteName() {
  const ogSiteName = document.querySelector('meta[property="og:site_name"]')?.content?.trim()
  if (ogSiteName) return ogSiteName
  const appName = document.querySelector('meta[name="application-name"]')?.content?.trim()
  if (appName) return appName
  return null
}

// 網域猜品牌名稱：品牌一定是緊接在 TLD 前面那一段（第二層網域，SLD），不管前面疊了幾層
// 子網域——一開始這裡直接拿網域最左邊那一段（只額外處理 www. 這個特例），結果
// "accounts.spotify.com" 被猜成「Accounts」而不是「Spotify」：www 只是恰好也位在最左邊，
// 不能代表「最左邊那一段就是該去掉的子網域」這個通則，子網域可以是 accounts、mail、
// checkout......任何東西。改成先找出 TLD 佔幾段（含多段式 TLD），倒數第二段（TLD 前面
// 那段）才是真正的品牌名稱。純 ASCII 才做首字母大寫，中文/日文這類非拉丁字元網域大寫化
// 沒有意義，維持原樣。這只是 readPageSiteName 找不到時的保底猜測，猜不準是預期內的限制，
// 使用者本來就可以自己在表單上改標題。
function deriveTitleFromDomain(domain) {
  if (!domain) return ''

  const host = domain.toLowerCase()
  let tldLabelCount = 1
  for (const tld of MULTI_PART_TLDS) {
    if (host.endsWith(`.${tld}`)) {
      tldLabelCount = tld.split('.').length
      break
    }
  }

  const labels = host.split('.')
  const brandIndex = labels.length - tldLabelCount - 1
  const brand = brandIndex >= 0 ? labels[brandIndex] : labels[0]

  if (/^[a-z0-9-]+$/i.test(brand)) {
    return brand.charAt(0).toUpperCase() + brand.slice(1)
  }
  return brand
}
