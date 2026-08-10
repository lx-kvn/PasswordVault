// options.js — 獨立設定頁（[Chrome 標準的 options_ui，open_in_tab: true，開一個完整分頁，
// 不是嵌在 chrome://extensions 裡的小面板]），語言切換＋預設電子信箱集中在這裡，
// popup 只留「設定」這顆按鈕呼叫 chrome.runtime.openOptionsPage() 開這頁。

const languageSelect = document.getElementById('languageSelect')
const defaultEmailInput = document.getElementById('defaultEmailInput')
const saveButton = document.getElementById('saveButton')
const statusEl = document.getElementById('status')

function setStatus(message) {
  statusEl.textContent = message || ''
}

// 語言切換立即生效＋立即存檔，不用等按「儲存」——「儲存」按鈕只對應下面的預設電子信箱
// 這種自由輸入的文字欄位，語言是選單式選擇，切了就是切了，跟大多數設定頁的慣例一致。
languageSelect.addEventListener('change', async () => {
  await setLanguage(languageSelect.value)
  applyTranslations(languageSelect.value)
})

saveButton.addEventListener('click', async () => {
  await setDefaultEmail(defaultEmailInput.value.trim())
  const lang = await getLanguage()
  setStatus(t('saved', lang))
})

;(async () => {
  const lang = await getLanguage()
  languageSelect.value = lang
  applyTranslations(lang)
  defaultEmailInput.value = await getDefaultEmail()
})()
