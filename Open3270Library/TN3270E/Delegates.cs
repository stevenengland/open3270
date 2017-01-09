namespace StEn.Open3270.TN3270E
{
	internal delegate void TelnetDataDelegate(object parentData, TNEvent eventType, string text);

	internal delegate void SChangeDelegate(bool option);

	public delegate void RunScriptDelegate(string where);
}
