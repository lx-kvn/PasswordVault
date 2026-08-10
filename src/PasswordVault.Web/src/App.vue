<script setup>
// PasswordVault.exe 的前端進入點——跟 FileLocker.Web 的 App.vue 不同，這裡刻意保持極簡：
// 密碼庫是這支程式唯一的功能，不需要分頁切換／其他功能的殼，直接掛共用套件的
// <PasswordLockerPage>（見 FileLocker repo docs/adr/0004-shared-password-locker-ui-npm-package.md）。
import { PasswordLockerPage } from '@lx-kvn/password-locker-ui'
import '@lx-kvn/password-locker-ui/style.css'
import { sendMessage, requestMessage } from './composables/useIpc.js'

// TODO：語言設定要接到 PasswordVault.exe 的設定檔（見 PasswordVault_獨立化_規劃.md 第 15 節
// 「設定不共享」，這輪還沒有設定頁 UI），先固定 zh-TW。
const lang = 'zh-TW'
</script>

<template>
  <PasswordLockerPage :lang="lang" :send-message="sendMessage" :request-message="requestMessage" />
</template>
