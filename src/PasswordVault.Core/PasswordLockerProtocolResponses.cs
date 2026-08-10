namespace PasswordVault.Core;

/// <summary>
/// 對應密碼庫的身份驗證回應——刻意跟 Core 的 PasswordLockerVerifyResult 不同形狀：
/// 不含 MasterKey。主金鑰只留在 PasswordLockerProtocolHandlers 背後的 PasswordLockerService
/// app session 記憶體裡，這個回應只送「成功與否」給前端，避免解密金鑰進到 WebView2 的 JS
/// 執行環境（見規劃文件第 11.2 節、PasswordLockerService._appSessionMasterKey 的說明）。
/// </summary>
public sealed record PasswordLockerVerifyResponse(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);
