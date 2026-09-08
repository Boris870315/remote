namespace Remote.Desktop.Protocols.Rdp;

public static class RdpDisconnectReasonFormatter
{
    public static string Format(int reason) => reason switch
    {
        3 => "伺服器因閒置逾時而中斷連線",
        4 => "登入程序逾時",
        5 => "此工作階段已被另一個連線取代",
        6 => "遠端桌面用戶端記憶體不足",
        7 => "伺服器拒絕連線",
        8 => "伺服器因安全性或 FIPS 原則拒絕連線",
        9 => "此帳號沒有足夠的遠端登入權限",
        10 => "伺服器要求重新輸入有效帳密",
        >= 256 and <= 267 => "遠端桌面授權失敗",
        768 => "帳號或密碼錯誤",
        >= 4096 and <= 32767 => $"RDP 協定錯誤（{reason}）",
        _ => $"RDP 連線已中斷（原因代碼 {reason}）",
    };
}
