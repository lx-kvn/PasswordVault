import zhTW from './locales/zh-TW.json'
import en from './locales/en.json'

const LOCALES = { 'zh-TW': zhTW, en }

// 找不到對應語言退回 zh-TW，再找不到就顯示 key 本身——跟 FileLocker.Web App.vue／
// FileLocker.Extension shared.js 的既有 t() 慣例一致（見 ADR-0004：套件自帶完整翻譯表，
// 只接受 lang prop，不需要外層透過 props 逐一傳字串進來）。
export function t(key, lang) {
  return LOCALES[lang]?.[key] ?? LOCALES['zh-TW'][key] ?? key
}
