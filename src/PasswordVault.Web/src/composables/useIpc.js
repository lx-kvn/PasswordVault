// 「怎麼跟 C# 講話」這一層——跟 FileLocker.Web 的 src/composables/useIpc.js 是同樣的薄包裝，
// 各自獨立一份（不是共用套件的一部分）：@lx-kvn/password-locker-ui 不假設宿主一定是
// WebView2，改成透過 props 接受這裡包好的 sendMessage／requestMessage 函式（見
// FileLocker repo docs/adr/0004-shared-password-locker-ui-npm-package.md）。
//
// pendingResolvers 用「回應訊息類型」當 key，不是用遞增 id 做請求關聯——同一種回應類型
// 同時間只會有一個等待中的請求，這是跟 FileLocker.Web 那份一致的既有假設。
const pendingResolvers = {}

export function sendMessage(type, payload = {}) {
  window.chrome.webview.postMessage({ type, ...payload })
}

export function requestMessage(requestType, responseType, payload = {}) {
  return new Promise((resolve) => {
    pendingResolvers[responseType] = resolve
    sendMessage(requestType, payload)
  })
}

export function resolvePending(responseType, data) {
  pendingResolvers[responseType]?.(data)
  delete pendingResolvers[responseType]
}

export function rejectAllPending(errorMessage) {
  for (const responseType of Object.keys(pendingResolvers)) {
    pendingResolvers[responseType]({ success: false, errorMessage })
    delete pendingResolvers[responseType]
  }
}
