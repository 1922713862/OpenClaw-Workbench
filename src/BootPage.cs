namespace JFWorkbench;

static class BootPage
{
    // Health comes from the native controller. A no-cors fetch cannot establish readiness.
    public const string Html = """
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>JFlash 工作台</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
*{box-sizing:border-box}html,body{height:100%;margin:0}body{background:#f9faf6;color:#243a33;font-family:"Microsoft YaHei UI","Segoe UI",sans-serif;display:flex;align-items:center;justify-content:center}.card{width:min(540px,86vw);text-align:center;padding:46px 35px}.mark{width:57px;height:57px;margin:0 auto 26px;border-radius:18px;background:#e8efdf;display:grid;place-items:center}.mark svg{width:25px;height:25px;fill:#6c8961}h1{font-size:27px;font-weight:600;letter-spacing:-.4px;margin:0 0 14px}.sub{color:#839078;font-size:16px;line-height:1.9;min-height:49px}.hint{background:#eef2e8;border:1px solid #e2e9db;border-radius:8px;color:#819073;font-size:15px;line-height:1.8;padding:14px 20px;margin:28px 0 0}.t{margin-top:24px;color:#a0aa94;font-size:14px}.pulse{display:inline-block;width:6px;height:6px;border-radius:50%;background:#8ca177;margin-right:8px;animation:pulse 2s ease-in-out infinite}@keyframes pulse{50%{opacity:.35}}.foot{margin-top:32px;font-size:14px;color:#a5ad9c}
</style></head><body><main class="card">
<div class="mark"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M13.4 2 5 13.1h5.4L9.7 22 19 9.6h-5.8z"/></svg></div>
<h1 id="title">等待本机网关连接</h1>
<div class="sub" id="detail">正在连接本机生产网关。可以切换到左侧“网关状态”查看详情。</div>
<div class="hint" id="hint">冷启动可能需要数分钟。就绪后会自动进入原有 OpenClaw 工作台。</div>
<div class="t"><span class="pulse"></span><span id="elapsed">已等待 0 秒</span></div>
<div class="foot">网关控制与运行日志始终可以从左侧访问</div>
</main><script>
const since=Date.now();setInterval(()=>{const n=Math.floor((Date.now()-since)/1000);document.getElementById('elapsed').textContent='已等待 '+(n<60?n+' 秒':Math.floor(n/60)+' 分 '+n%60+' 秒');},1000);
window.updateGatewayState=s=>{document.getElementById('title').textContent=s.title||'等待本机网关连接';document.getElementById('detail').textContent=s.detail||'正在检查本机状态';document.getElementById('hint').textContent=s.hint||'网关由控制面 jfctl 管理；可从左侧查看运行日志。';};
</script></body></html>
""";
}

