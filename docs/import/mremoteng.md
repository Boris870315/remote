# mRemoteNG 連線資料匯入

Remote 將 mRemoteNG 檔案視為「連線資料來源」，不是 ID Card 檔案。

## 舊版輸出格式確認

依 `remote old` 的 `mRemoteNG/App/Export.cs` 與 XML serializer，mRemoteNG 可輸出完整連線樹、選取的資料夾或單一連線，格式包含 mRXML 與 mRCSV。XML 的 `Node` 會保存資料夾／連線類型、階層、名稱、主機、協定、連接埠、帳號、密碼、網域、繼承旗標，以及各協定的顯示、重新導向、Gateway 等設定。輸出時可選擇是否包含帳號、密碼、網域、繼承與已指派認證。

## Remote 的匯入規則

- 主要匯入物件是資料夾與連線；保持階層、主機、協定、連接埠及目前支援的協定設定。
- 匯入連線資料不要求先解鎖 Vault，也不受 Vault 權限阻擋。
- Vault 鎖定時仍完成資料夾與連線匯入，但不把明文或解密後的帳密留在記憶體模型或連線設定中。
- Vault 已解鎖時，相同帳密會去重後轉換成加密 ID Card，再由連線直接引用或依原始繼承規則由資料夾提供。
- ID Card 是 Remote 內部的安全帳密物件，不是 mRemoteNG 匯入的主體。

## 第一版範圍

第一版支援 mRXML／`confCons.xml`，並可立即使用匯入的 RDP 與 VNC 連線。SSH2、HTTP、HTTPS 與 Telnet 的欄位對應可保留供下一版啟用，但第一版不得將它們顯示為可開啟的 Session。無法辨識的協定會略過並回報警告。mRCSV 與尚未映射的進階協定欄位列為後續相容性工作，不應被 UI 誤標為已支援。
