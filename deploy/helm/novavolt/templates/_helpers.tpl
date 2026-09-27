{{- define "novavolt.labels" -}}
app.kubernetes.io/part-of: {{ .root.Values.solution.name }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
app.kubernetes.io/version: {{ .root.Values.solution.version | quote }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end -}}

{{- define "novavolt.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{- /* Biến không bí mật từ values.config, cộng Secret "<release>-<workload>" do người vận hành tạo. */ -}}
{{- define "novavolt.env" -}}
{{- with (index .root.Values.config .name) }}
env:
{{- range $key, $value := . }}
  - name: {{ $key }}
    value: {{ $value | quote }}
{{- end }}
{{- end }}
envFrom:
  - secretRef:
      name: {{ printf "%s-%s" .root.Release.Name .name }}
{{- end -}}

{{- define "novavolt.securityContext" -}}
securityContext:
  runAsNonRoot: true
  allowPrivilegeEscalation: false
  capabilities:
    drop: ["ALL"]
{{- end -}}
