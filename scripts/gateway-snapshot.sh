# Doc trang thai THAT cua edge gateway. Source file nay, dung chay truc tiep.
#
# Progress log cua gateway duoc lay mau moi `LogEvery` message, nen mot lab doc `buffer_depth` tu
# log khong phan biet duoc "hang doi da rong" voi "flusher chua kip ghi log tu luc no rong". Do la
# ly do 185 giay cua D3 khong dung lam bang chung: no co the la luc detector het ngan sach.
#
# `gateway.stats` duoc GatewayDiagnosticsWriter ghi bang write-tmp-rename moi 250 ms, nen nguoi doc
# luon thay mot snapshot hoan chinh — ban cu hoac ban moi, khong bao gio la ban vua ghi do dang.

NVM_GATEWAY_CONTAINER=${NVM_GATEWAY_CONTAINER:-nvm-edge-gateway}
NVM_GATEWAY_STATS=${NVM_GATEWAY_STATS:-/var/lib/nvm-edge-gateway/buffer/gateway.stats}

# Ca snapshot trong mot lan doc.
#
# Doc tung key bang mot `docker exec` rieng lay ve nhung con so o nhung thoi diem khac nhau, va
# tren mot duong ong dang chay thi `forwarded` doc sau co the LON HON `decoded` doc truoc — mot
# phep so sanh vo nghia bao thanh mat du lieu. Bat ky hai counter nao se duoc tru cho nhau deu
# phai den tu CUNG mot snapshot.
gateway_snapshot() {
	docker exec "$NVM_GATEWAY_CONTAINER" cat "$NVM_GATEWAY_STATS" 2>/dev/null | tr -d '\r'
}

# In gia tri cua mot key trong mot snapshot da doc.
snapshot_field() {
	printf '%s\n' "$1" | sed -n "s/^$2=//p" | head -1
}

# Tien loi cho phep doc mot key le, khi khong co gi se duoc tru cho no.
gateway_stat() {
	snapshot_field "$(gateway_snapshot)" "$1"
}

# Doi den khi gateway ghi snapshot dau tien. Tra 1 neu qua han.
gateway_wait_snapshot() {
	budget=${1:-120}
	start=$(date +%s)

	while [ "$(( $(date +%s) - start ))" -le "$budget" ]; do
		[ -n "$(gateway_stat captured_at)" ] && return 0
		sleep 1
	done

	return 1
}

# Muc day THUONG TRUC cua mot duong day dang chay, do bang dinh quan sat duoc trong `samples` giay.
#
# Tren mot day chuyen con phat, hang doi khong bao gio rong: flusher doc tung batch 128 record con
# thiet bi thi van gui. Do duoc chu ky 3 -> 128 -> 3 lap lai, va `depth = 0` chi xuat hien khoang
# 5% so lan lay mau. Mot lab doi `depth = 0` o day khong do "backlog da tieu het" — no do do may.
gateway_steady_depth() {
	samples=${1:-12}
	peak=0
	i=0

	while [ "$i" -lt "$samples" ]; do
		depth=$(gateway_stat buffer_depth)

		case "$depth" in
			''|*[!0-9]*) ;;
			*) [ "$depth" -gt "$peak" ] && peak=$depth ;;
		esac

		i=$((i + 1))
		sleep 1
	done

	printf '%s' "$peak"
}

# Doi hang doi ben vung ve muc <= `ceiling`.
#
# Tra 0 CHI KHI thuc su doc duoc mot muc dat yeu cau trong ngan sach; het gio la tra 1. Ban cu cua
# `reconcile.sh` ket thuc bang `return 0` nen mot lan xa that bai van di tiep va D1 co the bao dat
# ma khong ai biet duong ong con hang.
gateway_drain_to() {
	budget=$1
	ceiling=$2
	start=$(date +%s)

	while [ "$(( $(date +%s) - start ))" -le "$budget" ]; do
		depth=$(gateway_stat buffer_depth)

		case "$depth" in
			''|*[!0-9]*) ;;
			*) [ "$depth" -le "$ceiling" ] && return 0 ;;
		esac

		sleep 1
	done

	return 1
}

# Rong han. Dung khi nguon da dung — luc do va chi luc do, `depth = 0` la mot trang thai on dinh.
gateway_drain() {
	gateway_drain_to "${1:-180}" 0
}
