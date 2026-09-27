#!/bin/sh
# Mutation test domain (M13). Cần: dotnet tool install -g dotnet-stryker
# Chạy trên tests/Mutation/Nvm.DomainMutationTests (xunit v2, VSTest) — xem comment trong csproj vì sao không dùng
# project unit test chính.
#
# Phạm vi mặc định là "domain layer" theo ADR-050: thư mục Entities/ của mỗi FB (aggregate, luật, value object).
# Handlers/ là tầng application, được kiểm bằng integration test mà Stryker không chạy được; đặt
# NVM_MUTATE="**/*.cs" để đo cả FB. Báo cáo JSON ở StrykerOutput/ của từng lượt; dòng cuối là điểm gộp.
set -e
cd "$(dirname "$0")/../../tests/Mutation/Nvm.DomainMutationTests"
mutate="${NVM_MUTATE:-**/Entities/**/*.cs}"
out="${NVM_MUTATION_OUT:-StrykerOutput/run}"
for block in ${*:-Equipment Passport Quality Grading Traceability Material ProductionExecution Recipe MasterData FactoryModel}; do
  echo "== Nvm.$block"
  dotnet stryker --project "Nvm.$block.csproj" --mutate "$mutate" --reporter progress --reporter json \
    --output "$out/$block"
done
# Điểm gộp = (killed + timeout) / (killed + timeout + survived + nocoverage), như Stryker tính cho từng project.
for py in python3 python; do
  if "$py" -c "" 2>/dev/null; then break; fi
done
"$py" - "$out" <<'EOF'
import glob, json, sys
from collections import Counter
total = Counter()
for path in sorted(glob.glob(sys.argv[1] + "/*/reports/mutation-report.json")):
    c = Counter(m["status"] for f in json.load(open(path, encoding="utf-8"))["files"].values() for m in f["mutants"])
    k, s = c["Killed"] + c["Timeout"], c["Survived"] + c["NoCoverage"]
    total.update(c)
    print(f"{path.replace(chr(92), '/').split('/')[-3]}: {100 * k / max(k + s, 1):.2f} % ({k}/{k + s})")
k, s = total["Killed"] + total["Timeout"], total["Survived"] + total["NoCoverage"]
print(f"TOTAL: {100 * k / max(k + s, 1):.2f} % ({k}/{k + s})")
EOF
