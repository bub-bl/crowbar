// Analyze an Ultra profiler JSON export: aggregate self + inclusive time per function.
// Usage: node tools/analyze_profile.mjs <path-to-json> [--top N]
import fs from 'node:fs';

const path = process.argv[2];
const topN = Number(process.argv.find(a => a.startsWith('--top='))?.split('=')[1] ?? 40);
const d = JSON.parse(fs.readFileSync(path, 'utf8'));

const libNames = d.libs.map(l => l.name);

function analyzeThread(t) {
  const { samples, stackTable, frameTable, funcTable, stringArray, resourceTable } = t;
  const n = samples.length;
  // funcName cache
  const funcName = (fi) => {
    if (fi === null || fi === undefined) return '<unknown>';
    return stringArray[funcTable.name[fi]] ?? `<func#${fi}>`;
  };
  const funcLib = (fi) => {
    const r = funcTable.resource[fi];
    if (r === null || r === undefined) return null;
    const lib = resourceTable.lib[r];
    return lib !== undefined ? libNames[lib] : null;
  };
  const selfTime = new Map();   // func -> cpuTime (ns)
  const selfCount = new Map();  // func -> sample count
  const incTime = new Map();    // func -> inclusive cpuTime (ns) — per sample, add weight to every frame on the path
  const children = new Map();   // func -> Map(childFunc -> cpuTime)
  // also track stacks as func id lists for aggregation; simpler: accumulate per sample

  for (let i = 0; i < n; i++) {
    const weight = samples.threadCPUDelta?.[i] ?? (samples.timeDeltas?.[i] ?? 1) * 1e6;
    const stack = samples.stack[i];
    if (stack === null || stack === undefined) continue;
    // walk up the stack via prefix
    let s = stack;
    let first = true;
    const pathFuncs = [];
    while (s !== null && s !== undefined) {
      const frame = stackTable.frame[s];
      const func = frameTable.func[frame];
      pathFuncs.push(func);
      if (first) {
        selfTime.set(func, (selfTime.get(func) ?? 0) + weight);
        selfCount.set(func, (selfCount.get(func) ?? 0) + 1);
        first = false;
      }
      s = stackTable.prefix[s];
    }
    // inclusive: add weight to each func on path
    let parent = null;
    for (let k = pathFuncs.length - 1; k >= 0; k--) {
      const f = pathFuncs[k];
      incTime.set(f, (incTime.get(f) ?? 0) + weight);
      if (parent !== null) {
        if (!children.has(parent)) children.set(parent, new Map());
        const cm = children.get(parent);
        cm.set(f, (cm.get(f) ?? 0) + weight);
      }
      parent = f;
    }
  }
  return { selfTime, selfCount, incTime, children, funcName, funcLib };
}

// Aggregate across threads
const aggSelf = new Map();
const aggCount = new Map();
const aggInc = new Map();
let totalSamples = 0;
let totalCpuNs = 0;
for (const t of d.threads) {
  const a = analyzeThread(t);
  for (const [f, v] of a.selfTime) aggSelf.set(f, (aggSelf.get(f) ?? 0) + v);
  for (const [f, v] of a.selfCount) aggCount.set(f, (aggCount.get(f) ?? 0) + v);
  for (const [f, v] of a.incTime) aggInc.set(f, (aggInc.get(f) ?? 0) + v);
  totalSamples += t.samples.length;
  totalCpuNs += t.samples.threadCPUDelta?.reduce((a, b) => a + b, 0) ?? 0;
}

// names from first thread (they share the same stringArray usually)
const nameOf = (fi) => d.threads[0].stringArray[d.threads[0].funcTable.name[fi]] ?? `<func#${fi}>`;
const libOf = (fi) => {
  const r = d.threads[0].funcTable.resource[fi];
  if (r == null) return null;
  const lib = d.threads[0].resourceTable.lib[r];
  return lib !== undefined ? libNames[lib] : null;
};

const ms = (ns) => (ns / 1e6).toFixed(1);
const pct = (ns) => ((ns / totalCpuNs) * 100).toFixed(1);

console.log(`Threads: ${d.threads.length}, total samples: ${totalSamples}, total CPU: ${(totalCpuNs / 1e9).toFixed(2)} s`);
console.log('\n=== TOP SELF TIME (leaf) ===');
const selfSorted = [...aggSelf.entries()].sort((a, b) => b[1] - a[1]).slice(0, topN);
for (const [f, v] of selfSorted) {
  console.log(`${ms(v).padStart(9)} ms (${pct(v).padStart(5)}%) n=${(aggCount.get(f) ?? 0).toString().padStart(6)}  ${nameOf(f)}  [${libOf(f) ?? '?'}]`);
}

console.log('\n=== TOP INCLUSIVE TIME (with children) ===');
const incSorted = [...aggInc.entries()].sort((a, b) => b[1] - a[1]).slice(0, topN);
for (const [f, v] of incSorted) {
  console.log(`${ms(v).padStart(9)} ms (${pct(v).padStart(5)}%)  ${nameOf(f)}  [${libOf(f) ?? '?'}]`);
}

// For the top inclusive functions, show children breakdown
console.log('\n=== CHILDREN of top inclusive functions ===');
// recompute children aggregated: need per-thread children; let's do a simpler global: for top funcs, print self fraction
for (const [f, v] of incSorted.slice(0, 12)) {
  const self = aggSelf.get(f) ?? 0;
  console.log(`\n${nameOf(f)} — inc ${ms(v)} ms (${pct(v)}%), self ${ms(self)} ms (${pct(self)}%)`);
}
