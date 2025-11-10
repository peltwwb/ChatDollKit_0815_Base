using UnityEngine;
using System; //DateTimeを使用する為追加。

public class ExitGame : MonoBehaviour
{

     //DateTimeを使うため変数を設定
    DateTime TodayNow;

    public void Quit()
    {
        TodayNow = DateTime.Now;
        Debug.Log(TodayNow.Year.ToString() + "年 " + TodayNow.Month.ToString() + "月" + TodayNow.Day.ToString() + "日" + DateTime.Now.ToLongTimeString());
        Debug.Log(Time.time);
        Application.Quit();
    }
}