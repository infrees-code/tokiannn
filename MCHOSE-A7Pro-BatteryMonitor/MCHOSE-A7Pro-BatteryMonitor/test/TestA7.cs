using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MouseBattery
{
    /// <summary>控制台自测 harness：直接调用 HidBatteryReader，把结果写到文件（供开发验证用）</summary>
    internal static class TestA7
    {
        static void Main(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== HidBatteryReader 自测 ===");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            try
            {
                List<BatteryReading> list = HidBatteryReader.ReadAll(true);
                sb.AppendLine("枚举到的电池设备数: " + list.Count);
                foreach (BatteryReading r in list)
                {
                    sb.AppendLine(string.Format(
                        "  VID_{0:X4} PID_{1:X4}  pct={2,4}  充电={3,-5}  来源={4}  名称={5}",
                        r.Vid, r.Pid, r.Percent, r.Charging, r.Source, r.DeviceName));
                }

                BatteryReading best = HidBatteryReader.PickBest(list);
                sb.AppendLine();
                sb.AppendLine(best != null
                    ? string.Format("最终选择: {0} = {1}%  充电={2}  来源={3}",
                        best.DeviceName, best.Percent, best.Charging, best.Source)
                    : "最终选择: (未检测到)");
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex);
            }

            string path = args.Length > 0 ? args[0] : "test_a7_result.txt";
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }
    }
}
