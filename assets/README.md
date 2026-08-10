# assets/

`icon-source.png`：PasswordVault 的品牌主圖示（金色鑰匙，白底圓角方形），1024×1024。

從這份主圖重新產生各處用到的圖示檔：

- `src/PasswordVault.App/icon.ico`：WPF `ApplicationIcon`，PNG 內嵌的多解析度 ICO（16／32／48／256）。
- `src/PasswordVault.Extension/icons/{16,48,128}.png`：瀏覽器擴充功能圖示。

兩邊都是直接從 `icon-source.png` 縮放產生，不是分開繪製——之後主圖示改版，只要重新跑一次縮放/ICO 組裝就能同步更新所有地方，不用逐一手動改。
