#!/bin/sh
# Mutation test domain (M13). Cần: dotnet tool install -g dotnet-stryker
# Chạy trên tests/Mutation/Nvm.DomainMutationTests (xunit v2, VSTest) — xem comment trong csproj vì sao không dùng
# project unit test chính. Báo cáo JSON ở StrykerOutput/ của từng lượt.
set -e
cd "$(dirname "$0")/../../tests/Mutation/Nvm.DomainMutationTests"
for block in ${*:-Equipment Passport Quality Grading Traceability}; do
  echo "== Nvm.$block"
  dotnet stryker --project "Nvm.$block.csproj" --reporter progress --reporter json --reporter html
done
