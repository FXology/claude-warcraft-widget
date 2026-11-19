// Claude Code status line command. Saves the plan's rate limits and each chat's context use for the widget,
// and prints a short status line.
// Docs: https://code.claude.com/docs/en/statusline (rate_limits is only sent for Pro/Max plans, after the first reply).
const fs = require('fs');
const path = require('path');

const dir = path.join(process.env.LOCALAPPDATA, 'ClaudeUsageWidget');

// Write then rename, so the widget never reads half a file (several sessions may write at once).
function save(file, data) {
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = `${file}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify({ ...data, updated_at: Math.floor(Date.now() / 1000) }));
    fs.renameSync(tmp, file);
  } catch {} // the status line must still print if the file is busy
}

let input = '';
process.stdin.on('data', chunk => (input += chunk)).on('end', () => {
  let data = {};
  try { data = JSON.parse(input); } catch {}
  const limits = data.rate_limits;
  const ctx = data.context_window?.used_percentage;

  if (limits) save(path.join(dir, 'usage.json'), { rate_limits: limits });
  // One file per chat, so parallel sessions never overwrite each other. The id becomes a file name, so it must be plain.
  if (ctx != null && /^[\w-]+$/.test(data.session_id ?? '')) save(path.join(dir, 'context', `${data.session_id}.json`), { used_percentage: ctx });

  const pct = n => `${Math.round(n)}%`;
  process.stdout.write([
    data.model?.display_name,
    ctx != null && `ctx ${pct(ctx)}`,
    limits?.five_hour && `5h ${pct(limits.five_hour.used_percentage)}`,
    limits?.seven_day && `7d ${pct(limits.seven_day.used_percentage)}`,
  ].filter(Boolean).join(' · '));
});
