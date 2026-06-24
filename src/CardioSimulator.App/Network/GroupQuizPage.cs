namespace CardioSimulator.App.Network;

/// <summary>
/// The single-page mobile quiz client served by <see cref="GroupTestServer"/> at <c>GET /</c>. Plain
/// responsive HTML + vanilla JS (no framework, no external assets) so it loads instantly on any phone
/// on the LAN. Flow: registration form → fetch the generated questions → answer → submit → score.
/// Image stimuli load from <c>/api/image</c>; ECG stimuli show the question text only (no Win2D trace
/// on phones). RU-first, since the audience is Russian-speaking students.
/// </summary>
internal static class GroupQuizPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
<title>Тестирование</title>
<style>
  :root { --accent:#2176ff; --ok:#1ea03c; --bad:#d22828; }
  * { box-sizing:border-box; }
  body { font-family:-apple-system,Segoe UI,Roboto,sans-serif; margin:0; background:#f5f6f8; color:#1b1b1b; }
  .wrap { max-width:680px; margin:0 auto; padding:16px; }
  h1 { font-size:20px; margin:8px 0 16px; }
  label { display:block; font-size:13px; color:#555; margin:12px 0 4px; }
  input[type=text] { width:100%; padding:12px; font-size:16px; border:1px solid #ccc; border-radius:8px; }
  button { width:100%; padding:14px; font-size:17px; font-weight:600; color:#fff; background:var(--accent);
           border:0; border-radius:10px; margin-top:18px; cursor:pointer; }
  button:disabled { opacity:.5; }
  .card { background:#fff; border:1px solid #e3e3e3; border-radius:12px; padding:14px; margin:14px 0; }
  .qtext { font-weight:600; font-size:16px; margin-bottom:10px; }
  .opt { display:flex; align-items:flex-start; gap:10px; padding:11px; border:1px solid #ddd; border-radius:8px; margin:8px 0; }
  .opt input { margin-top:3px; transform:scale(1.3); }
  .opt.sel { border-color:var(--accent); background:#eef4ff; }
  .qimg { width:100%; border-radius:8px; margin-bottom:10px; }
  .note { color:#888; font-size:13px; font-style:italic; margin-bottom:10px; }
  .counter { color:#888; font-size:13px; }
  .score { text-align:center; padding:40px 16px; }
  .score .big { font-size:44px; font-weight:700; }
  .pass { color:var(--ok); } .fail { color:var(--bad); }
  .err { color:var(--bad); font-size:14px; margin-top:8px; }
</style>
</head>
<body>
<div class="wrap" id="app"></div>
<script>
const app = document.getElementById('app');
let token = null, questions = [];

function esc(s){ return (s||'').replace(/[&<>"]/g, c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

function showRegister(msg){
  app.innerHTML =
    '<h1>Регистрация</h1>' +
    '<label>ФИО</label><input id="fio" type="text" autocomplete="name">' +
    '<label>Группа</label><input id="grp" type="text">' +
    '<button id="go">Начать тест</button>' +
    (msg ? '<div class="err">'+esc(msg)+'</div>' : '');
  document.getElementById('go').onclick = register;
}

async function register(){
  const fio = document.getElementById('fio').value.trim();
  const grp = document.getElementById('grp').value.trim();
  if(!fio || !grp){ showRegister('Заполните ФИО и группу.'); return; }
  try {
    const r = await fetch('/api/register', {method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({fullName:fio, group:grp})});
    if(!r.ok) throw 0;
    const data = await r.json();
    token = data.token; questions = data.questions || [];
    if(questions.length === 0){ showRegister('Банк вопросов пуст — обратитесь к преподавателю.'); return; }
    showQuiz();
  } catch(e){ showRegister('Не удалось начать тест. Повторите.'); }
}

function showQuiz(){
  let html = '<h1>Тест <span class="counter">('+questions.length+' вопр.)</span></h1>';
  questions.forEach((q,qi)=>{
    html += '<div class="card"><div class="qtext">'+(qi+1)+'. '+esc(q.text)+'</div>';
    if(q.stimulus === 'image') html += '<img class="qimg" src="/api/image?token='+encodeURIComponent(token)+'&qid='+encodeURIComponent(q.id)+'" alt="">';
    else if(q.stimulus === 'ecg') html += '<div class="note">ЭКГ показана на экране преподавателя.</div>';
    (q.options||[]).forEach(o=>{
      const oid = 'o_'+qi+'_'+o.id;
      html += '<label class="opt" id="lbl_'+oid+'"><input type="radio" name="q_'+qi+'" value="'+esc(o.id)+'" onchange="sel('+qi+',\''+esc(o.id)+'\')"><span>'+esc(o.text)+'</span></label>';
    });
    html += '</div>';
  });
  html += '<button id="submit">Завершить и отправить</button>';
  app.innerHTML = html;
  document.getElementById('submit').onclick = submit;
}

const answers = {};
function sel(qi, oid){
  const q = questions[qi];
  answers[q.id] = oid;
  (q.options||[]).forEach(o=>{
    const lbl = document.getElementById('lbl_o_'+qi+'_'+o.id);
    if(lbl) lbl.classList.toggle('sel', o.id === oid);
  });
}

async function submit(){
  const btn = document.getElementById('submit'); btn.disabled = true;
  try {
    const r = await fetch('/api/submit', {method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({token: token, selections: answers})});
    if(!r.ok) throw 0;
    const res = await r.json();
    const cls = res.passed ? 'pass' : 'fail';
    app.innerHTML = '<div class="score"><div>Результат</div>' +
      '<div class="big '+cls+'">'+res.correct+' / '+res.total+'</div>' +
      '<div class="'+cls+'">'+(res.passed ? 'Зачёт' : 'Незачёт')+'</div>' +
      '<div class="note" style="margin-top:20px">Результат сохранён. Можно закрыть страницу.</div></div>';
  } catch(e){ btn.disabled = false; alert('Не удалось отправить. Повторите.'); }
}

showRegister();
</script>
</body>
</html>
""";
}
