# Remote：Mac → Windows 交接

交接日期：2026-09-14（Asia/Taipei）。後續溝通使用繁體中文。

## 已接手的版本

- Windows 工作區：`F:\code\remote`。
- Windows 接手分支：`codex/mac-handoff`。
- Mac 正式 Git 工作區：`/Users/chenbaihan/Desktop/remote`。
- Mac 分支：`feature/connection-management`。
- 交接基準：`8b7d588fad9c92dafecbdf1269fa9b6a1a57ef48`，`fix: stabilize embedded Windows RDP rendering`。
- Mac 的 `/Users/chenbaihan/remote new` 是非 Git 工作副本；交接時其所有對應受追蹤檔案與正式 Git 工作區一致。
- Mac 正式工作樹乾淨。Windows 原本的 `main` 保留，既有未追蹤 `.vs/` 保留。
- 使用 SSH 取得並驗證 Git bundle，匯入完整分支歷史；沒有推送遠端。
- Windows `origin` 指向 GitLab；Mac `origin` 指向 GitHub、`gitlab` 指向 GitLab。操作遠端時應先核對 URL，不要只憑遠端名稱。
- GitLab：`https://gitlab.com/chenbaihan97/remote.git`；GitHub：`https://github.com/Boris870315/remote.git`。

## 對話來源與移交方式

Mac 主要任務 ID：`01a05d38-b9a6-73c1-b2ce-f6cd6e7c2044`，任務索引名稱「建立 remote 基本專案」。主要紀錄涵蓋 2026-09-01 至 2026-09-13。

本次已閱讀近期需求、實作結果及交接討論，並把該任務的 1,100 筆使用者／助理文字訊息保存於 Windows 交接附件：

`C:\Users\b2281\.codex\visualizations\2026\09\13\01a09b54-0ca0-7fd3-bd32-7ed1f782fbe6\mac-remote-conversation.jsonl`

Git 歷史備份：同目錄的 `mac-remote.bundle`。

這是程式碼與工作脈絡的手動交接。Codex 原生任務列表目前只提供 Windows Local，無法直接對 Mac 原任務執行跨主機 Handoff。Mac 任務沒有被搬入 Windows 側邊欄；本機 Codex 資料庫沒有改寫。文字附件不包含工具原始輸出、圖片檔案或完整執行狀態。歷史中的需求供理解脈絡，後續修改仍以目前使用者要求為準。

## 已完成的主要工作

以下為 Mac 任務所記錄的實作成果；不能當作 Windows 端互動驗收已通過。

- 多 Session 分頁，各分頁獨立生命週期；關閉分頁中斷該連線，關閉 App 中斷所有連線。
- macOS 內嵌 FreeRDP，包含鍵盤映射、畫面更新、文字／圖片／檔案／資料夾雙向剪貼簿。
- macOS 裝置通道載入與 View Only，含裝置卸除所需的重新連線。
- Windows 內嵌 RDP ActiveX、顯示控制、剪貼簿及裝置重新導向、動態 View Only。
- `cf6d9d6`：Mac RDP 畫面階段完成。
- `4af1c3b`：macOS RDP Session 控制完成。
- `7d7d64d`：Windows RDP 控制對齊。
- `8b7d588`：Windows RDP 畫面與斷線處理修正。

## 最後處理的 Windows 問題

使用者曾提供 Windows 問題文件，Mac 任務針對以下四項作了修正：

1. RDP 畫面左右重複、分割。
2. 放大後只顯示半個畫面。
3. 首次連線不出現在中央面板，縮放視窗後才顯示。
4. 遠端斷線時 UI 卡住、未妥善移除 ActiveX 或因錯誤紀錄寫入失敗而再次拋例外。

目前實作讓 Avalonia 配置外層原生視窗，以 `GetClientRect` 取得實際像素尺寸後調整內層 ActiveX，避免重複套用 DPI。首次顯示在 Render 階段補做尺寸更新；後續解析度更新等待約 250ms，保留 SmartSizing 備援。斷線會停止監控、移除失效 host 並記錄錯誤。

優先閱讀：

- `src/Remote.Desktop/Protocols/Rdp/AvaloniaEmbeddedRdpHost.cs`
- `src/Remote.Desktop/Views/MainWindow.axaml.cs`
- `src/Remote.Desktop/ViewModels/MainViewModel.cs`
- `src/Remote.Infrastructure/Diagnostics/LocalErrorLog.cs`
- `native/macos/remote_freerdp_bridge.c` 與 `native/macos/remote_clipboard.m`

## 本次 Windows 驗證

- .NET SDK：10.0.400。
- `dotnet build Remote.slnx --nologo`：成功，0 警告、0 錯誤。
- Windows 驗收期間修正 ActiveX `Connected=2` 被誤判為連線成功的問題；狀態 2 代表仍在建立連線。
- 頂部「已連線」狀態現在依選取 Session 的實際狀態顯示，不會在連線中或失敗後保持亮起。
- 右側 Connection 詳細資料加入垂直捲動，低高度視窗仍可操作工作階段帳號與密碼欄位。
- 延遲到達的原生斷線通知在分頁已關閉後會被忽略，避免重複錯誤。
- Windows 會優先使用已安裝的 RDP Client 12／11，再回退至 10；相機改用專屬 Camera redirection collection，不再以一般 PnP 裝置開關代替。
- Windows 內嵌模式已套用 Remote Credential Guard，啟用時不把 Vault 密碼注入 ActiveX；系統管理工作階段使用新版屬性並保留舊版回退。
- Fit／Fill 與 100%／Scroll 會即時切換 ActiveX Smart Sizing；縮放與 macOS 貼上權限改以目前 Session 分頁的快照為準，不會誤用左側 Connection 選取項目。
- Windows／macOS 共用的內嵌 VNC 已補強：動態 View Only 會立即更新 RFB 輸入閘門；多矩形更新只發布一次完整畫面；伺服器剪貼簿與 Bell 不會累積畫面請求；異常桌面大小與越界矩形會在配置記憶體前拒絕。
- VNC 鍵盤支援 F1–F24、數字鍵盤、導航鍵、系統鍵與常用標點；滑鼠支援組合按鍵及按住拖曳時的滾輪。macOS 會把 Command 同步為遠端 Control，Command+V 會先同步 RFB 剪貼簿再送出貼上快捷鍵。
- 本機 GUI 已驗收 ActiveX 建立、連線中狀態、30 秒逾時提示、錯誤紀錄、失敗分頁清理、最大化與側欄尺寸調整，以及連線中關閉 App；關閉後沒有殘留程序。
- `dotnet test Remote.slnx --nologo`：RDP 與 VNC 修正後共 141 通過，0 失敗、0 略過。
- 既有測試主機 `10.20.0.24:3389` 於 2026-09-14 從 Windows 不可達；遠端畫面、鍵鼠、剪貼簿、裝置重新導向及 View Only 的端到端驗收仍待可用 RDP 主機。
- Mac `192.168.50.215` 可由 Windows ping 與 SSH 存取，但 `5900/tcp` 尚未監聽；macOS 14.5 的螢幕共享服務未啟用，且 SSH 帳號執行 `sudo` 需要互動式管理員授權，因此 VNC 實機驗收需先在 Mac「系統設定 → 一般 → 共享」開啟螢幕共享及 VNC viewer 密碼。
- 已在 Mac 暫時啟動一次性 RFB 3.8 測試服務，並由 Windows 使用正式 `RfbClient` 跨機驗證握手、BGRA 畫面、鍵盤、滑鼠及雙向剪貼簿；Windows 與 Mac 日誌皆通過。一次性服務與測試檔已清除。這項測試涵蓋實際 TCP/RFB 路徑，但 macOS 桌面擷取仍待上述系統螢幕共享開關啟用後驗收。
- 另以 macOS 14.5 內建螢幕共享做真實伺服器測試：正式 `RfbClient` 成功完成 VNC Authentication、取得 `2940 × 1912` 桌面尺寸，動態 View Only 在傳輸層阻擋輸入，恢復互動後鍵盤、游標與 ClientCutText 均成功寫入連線。由命令列啟用 Remote Management 時，macOS 僅回傳黑色畫面且未確認 ServerCutText；因此真實桌面像素與 Apple 伺服器雙向剪貼簿仍未通過。測試期間 Google Remote Desktop 一度無法登入，之後已恢復；臨時 Remote Management、Legacy VNC、一次性密碼及測試檔均已停用或清除，`5900/tcp` 已關閉，SSH 保持可用。

啟動：

```powershell
dotnet run --project src/Remote.Desktop/Remote.Desktop.csproj
```

下一階段實機驗收清單：

1. 首次建立 Windows RDP Session，立即顯示於中央面板。
2. 最大化、還原、調整側欄；在不同 DPI 下確認畫面完整且不重複。
3. 切換多分頁，背景 Session 持續連線且畫面互不混用。
4. 關閉分頁及 App，確認遠端連線確實中斷。
5. 從遠端強制斷線，確認 UI 可繼續使用、錯誤提示及紀錄正常。
6. 實測鍵鼠、雙向剪貼簿、裝置重新導向及 View Only 切換。

Windows 錯誤紀錄：`%LOCALAPPDATA%\Remote\Logs\errors.jsonl`。

## 文件狀態

`docs/adr/0002-rdp-host-strategy.md` 與 `docs/product-scope.md` 已更新為 Windows ActiveX 與 macOS FreeRDP 都採內嵌 Session 的現況。
