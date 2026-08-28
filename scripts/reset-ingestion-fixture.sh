#!/usr/bin/env sh
#
# Đưa fixture đo về trạng thái xác định. Source file này, đừng chạy trực tiếp.
#
# VÌ SAO PHẢI CÓ: D1 và D2 phát biểu bằng một con số tuyệt đối, nên chúng chỉ có nghĩa khi biết
# chúng được đo trên cái gì. Hai thứ đo được ngày 2026-08-29 làm số đo trôi:
#
#   1. Bảng phình theo từng lần chạy. `source_event_id` là hàm băm của natural key nên KHÔNG sắp
#      được theo thời gian; mỗi row là một lần chèn ngẫu nhiên vào B-tree. Sau vài lần chạy D2,
#      telemetry + processed_message đạt 18,3 triệu row và 10,4 GB — lớn hơn `shared_buffers` 768 MB
#      hơn mười lần. Cùng một commit đo được 4.981 msg/s rồi 4.758 msg/s, p95 2,60 s rồi 9,46 s.
#      Con số xấu đi vì fixture to ra, không vì code tệ đi.
#
#   2. Simulator nén thời gian (`TimeCompression`), nên nó phát device timestamp đi TRƯỚC đồng hồ
#      treo tường — đo được 307.104 row có device_timestamp tới tận 2026-09-10. Những row đó ngồi
#      sẵn trên natural key mà harness sẽ dùng lại khi thời gian thật đi tới đó, và dedup từ chối
#      row của harness. Đúng hành vi, nhưng nó làm vế phải của phép đối chiếu thiếu đi.
#
# KHÔNG vi phạm K4. K4 cấm ỨNG DỤNG sửa/xoá hồ sơ traceability. Đây là thao tác lab dựng lại
# fixture trước khi đo, cùng hạng với `make down-v` đã có sẵn — không phải đường code chạy trong
# production. Đặt NVM_KEEP_FIXTURE=1 để bỏ qua khi muốn đo trên fixture đang có.

reset_ingestion_fixture() {
	if [ "${NVM_KEEP_FIXTURE:-0}" = "1" ]; then
		printf '%s\n' "== GIU nguyen fixture (NVM_KEEP_FIXTURE=1) — so do se khong tai lap duoc"
		return 0
	fi

	before=$(docker exec nvm-timescale sh -c \
		'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT count(*) FROM ts.telemetry_measurement;"' \
		2>/dev/null | tr -d ' \r')

	# Một lệnh cho cả hai bảng: telemetry có foreign key sang processed_message, nên xoá riêng lẻ
	# sẽ hoặc hỏng ràng buộc hoặc phải theo đúng thứ tự. TRUNCATE cả hai là nguyên tử và trả lại
	# không gian ngay, khác với DELETE để lại bloat đúng thứ phép đo này đang muốn loại bỏ.
	docker exec nvm-timescale sh -c \
		'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q -c "
            TRUNCATE ts.telemetry_measurement, ingest.processed_message;"' >/dev/null 2>&1 || {
		printf '%s\n' "Khong TRUNCATE duoc fixture ingestion." >&2
		return 1
	}

	printf '%s\n' "== reset fixture: xoa ${before:-?} row telemetry, do tren bang rong"
	return 0
}
