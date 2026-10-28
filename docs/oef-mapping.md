# Bảng cơ chế nền v2

Mỗi dòng là một cơ chế của nền kiểu Opcenter EX mà Foundation phải tái hiện theo hành vi đã quan sát
([scope §3.1](scope.md#31-cơ-chế-foundation-phải-tái-hiện)). Cột **Test** ghi tên test chứng minh
hành vi; chưa có test thì chưa tính là xong.

| Trạng thái | Nghĩa |
|---|---|
| `chưa làm` | Chưa có code |
| `đang làm` | Có code, thiếu test cho ít nhất một hành vi |
| `xong` | Mọi hành vi trong dòng có test xanh |

| Cơ chế | Mốc | Trạng thái | Test |
|---|---|---|---|
| Domain, `Query`/`ProjectionQuery`, analyzer chặn dùng nhầm model | R1 | `xong` | `Query_sees_submitted_changes_but_projection_query_does_not`, `Query_of_another_domain_is_refused`, `Pre_check_in_another_domain_is_a_model_error`, NVM004 (`Nvm004CrossDomainQueryAnalyzerTests`) |
| Cột hệ thống, khóa logic, ràng buộc bắt buộc ở tầng model | R1 | `xong` | `Mandatory_entity_property_is_enforced_by_the_model_while_the_column_stays_nullable`, `System_columns_are_stamped_from_the_platform_clock`, `Table_names_are_truncated_with_a_numeric_suffix_and_recorded` |
| Xóa mềm bằng số đếm `IsDeleted` | R1 | `xong` | `Delete_then_recreate_with_the_same_NId_succeeds`, `Sql_join_without_IsDeleted_counts_deleted_rows_lab_L03` |
| Facet trên entity của lớp Product | R1–R2 | `xong` | `Facets_of_an_entity_share_one_table_with_sparse_columns`; import vào POM: `Entity_not_imported_by_the_app_cannot_be_read_there` |
| Kiểm model ở service layer (lỗi `6400`) | R1 | `xong` | `Missing_mandatory_parameter_returns_6400_without_running_the_handler`, `Command_not_published_by_the_app_is_refused` |
| Pre-check theo app | R1 | `xong` | `Pre_check_blocks_only_in_the_app_that_enables_it` |
| Post-action trong cùng transaction, áp mọi app | R1 | `xong` | `Post_action_failure_fails_the_command_and_writes_nothing`, `Post_action_runs_in_every_app`, `Optional_post_action_can_be_disabled_in_the_solution` |
| Transaction: chuỗi trong FB và composite không nguyên tử | R1 | `xong` | `Composite_keeps_the_first_call_when_the_second_call_fails`, `Call_command_chain_in_a_block_rolls_back_the_parent_when_the_child_fails`, `Call_command_to_another_domain_commits_on_its_own`, `Commit_failure_publishes_no_event_and_keeps_nothing`, `Commit_failure_can_still_report_succeeded_for_lab_L02` |
| Dạng lỗi trả về (handler, service layer) | R1 | `xong` | `Handler_error_uses_the_error_and_succeeded_shape`, `Missing_mandatory_parameter_returns_6400_without_running_the_handler`, `Pre_check_blocks_only_in_the_app_that_enables_it` |
| Trace theo `CorrelationId` | R1 | `xong` | `Trace_filtered_by_correlation_id_holds_the_whole_chain`, `Trace_of_a_successful_call_contains_the_commit` |
| Event phát sau commit | R1–R2 | `xong` | `Event_is_stored_and_published_after_commit`, `Commit_failure_publishes_no_event_and_keeps_nothing`, `Plant_subscription_runs_the_handler_after_the_response`, `Relay_publishes_events_left_behind_while_the_bus_was_down` |
| Subscription, worker role | R2 | `xong` | `Event_without_subscription_is_published_and_nobody_handles_it_lab_L04`, `Plant_subscription_runs_the_handler_after_the_response`, `Worker_role_sets_how_many_consumers_take_events` |
| Reading function, reading model là view | R2 | `xong` | `Reading_function_returns_a_deferred_query_that_odata_keeps_filtering`, `Missing_view_gives_http_200_with_a_cut_body_lab_L15` |
| App, POM, OData theo app, token có hạn | R2 | `xong` | `Entity_set_applies_filter_order_top_and_count`, `Single_entity_is_read_by_its_id`, `Command_is_posted_by_its_published_name`, `Expired_token_is_refused_with_6401_lab_L05`, `Another_plant_in_the_url_is_not_served` |
| Extension app: tiền tố, phẳng | R2 | `xong` | `Extension_app_command_runs_under_the_base_app_with_its_prefix`, `Extension_app_cannot_extend_an_extension_app` |
| Signal rule, timer | R2 | `xong` | `Timer_rule_fires_once_per_interval_including_missed_ticks`, `One_shot_timer_fires_once_and_disables_itself`, `Event_rule_maps_event_properties_into_the_command`, `Event_rule_calls_a_command_when_the_event_arrives` |
| Configuration key | R2 | `xong` | `Configuration_key_is_stored_as_text_with_type_and_category` |
| State machine dạng dữ liệu, role theo transition | R2 | `xong` | `Status_added_as_data_is_usable_without_code`, `Transition_with_roles_needs_one_of_them_and_one_without_roles_is_open`, `Transition_bound_to_a_role_nobody_has_blocks_everyone_lab_L06`, `Roles_in_the_token_reach_the_state_machine` |
| Extension app công bố POM của nó dưới base app (tiền tố cho command) | R4 | `xong` | `Block_command_imported_by_an_extension_app_is_published_on_the_base_app_with_its_prefix` |
| Quyền gọi command theo role ở service layer | R4 | `xong` | `Restricted_app_lets_a_command_through_only_for_its_roles_the_administrator_and_the_system`, `Commands_follow_the_roles_of_the_manifest_and_the_administrator_may_call_everything` |
| Gửi tin ra ngoài đồng bộ và bất đồng bộ | R11 | `chưa làm` | |
| Gói deploy, cập nhật database, master data theo thứ tự | R12 | `chưa làm` | |

Test nằm ở `tests/Integration/Nvm.FoundationTests` (SQL Server, RabbitMQ thật qua Testcontainers), trừ NVM004.

## Lớp Product

Hành vi của lớp nhà cung cấp (R3, [ADR-056](adr/ADR-056-lop-product-r3.md)). Test ở `tests/Integration/Nvm.ProductTests`;
`Every_published_command_is_exercised_by_a_test` ép mỗi command của app `Production` có test gọi nó,
`Every_entity_set_of_the_production_app_is_readable` ép mọi entity đọc được qua đường OData.

| Hành vi | Nguồn | Test |
|---|---|---|
| Cây thiết bị nhiều cấp, con sâu hơn cha; property và nhóm (logical dock) | `load-register`, plan §4.3 | `Equipment_child_must_sit_deeper_than_its_parent`, `Equipment_property_and_group_are_upserted_by_key` |
| Material có cờ serial; một revision hiện hành | `material-mtu-vat-chua` | `Only_one_material_revision_is_current`, `Serialized_material_gives_quantity_one_and_batch_keeps_its_quantity` |
| BoP/Process/Operation: thứ tự, máy được phép, định mức | `ban-thiet-ke-va-ca` | `Process_defaults_its_bop_to_its_own_name_and_orders_operations_by_sequence` |
| MTU serial số lượng 1, lô có số lượng; hàng chưa có chỗ ở `n/a` | `material-mtu-vat-chua` | `Serialized_material_gives_quantity_one_and_batch_keeps_its_quantity` |
| Lịch sử MTU ghi Create, Assemble, Edit | `ban-thiet-ke-va-ca` (As Built), plan R3 DoD | `History_records_create_assemble_and_edit_in_order` |
| Trong vật chứa thì ô equipment trống, vị trí là của tầng ngoài | `material-mtu-vat-chua` | `Location_inside_a_container_is_the_equipment_of_the_outermost_container`, `Removing_from_a_container_puts_the_item_on_equipment_and_move_records_an_edit` |
| Gắn MTU thẳng vào pallet có tote: lớp Product chỉ gắn | `Báo cáo thực hành pallet có NC 2026-10-27` (plan R3) | `Assembling_an_mtu_straight_onto_a_pallet_with_totes_only_attaches_it` |
| Tách lô ra MTU con `.N` | `hang-loi` | `Split_creates_child_dot_n_with_the_split_quantity` |
| WO từ process: bước WO, máy dự kiến, nguyên liệu định sẵn | `ban-thiet-ke-va-ca` | `Work_order_from_process_copies_operations_planned_machines_and_materials` |
| Trạng thái WO theo dữ liệu; thêm Frozen, role cho Frozen → Aborted | `ban-thiet-ke-va-ca` | `Status_follows_the_data_driven_machine_and_raises_an_event_after_commit`, `Frozen_added_as_data_and_abort_from_frozen_needs_the_assigned_role` |
| Ép trạng thái WO; sửa, xóa WO | plan §4.3 | `Forced_status_skips_transitions_but_not_unknown_statuses`, `Edit_changes_plan_and_delete_is_soft_so_the_name_can_be_reused` |
| Ghi sản phẩm vào WO không tự cộng số lượng | plan R3 | `Recording_output_does_not_add_to_the_produced_quantity_unless_an_increment_is_given` |
| Tiến độ bước WO; trừ nguyên liệu ghi Assemble (genealogy) | `ban-thiet-ke-va-ca`, `hang-loi` | `Operation_progress_sets_the_step_status_and_machine`, `Consumption_lowers_the_lot_and_writes_assemble_genealogy_on_the_product` |
| NC OPEN giữ MTU; SCRAP hủy; chỉ OPEN đổi được | `hang-loi` | `Open_nc_holds_the_mtu_and_scrap_disposition_scraps_it` |
| USE AS IS thả MTU khi NC OPEN cuối cùng đóng | `hang-loi` | `Use_as_is_releases_the_mtu_only_when_its_last_open_nc_closes` |
| Hủy hàng loạt tạo NC SCRAP, NC OPEN cũ vẫn mở | `hang-loi` | `Scrap_at_creation_scraps_the_list_and_leaves_the_existing_open_nc_open` |
| MTU đã Scrapped bị từ chối | `hang-loi` | `Scrapped_mtu_is_rejected_by_a_new_nc_and_by_hold` |
| Containment: số tự sinh, OnHold + In Containment, Scrapped giữ nguyên, Ready/Editing, event lúc nhập | `containment-request` | `Importing_containment_numbers_it_holds_and_flags_the_mtus_and_raises_an_event` |
| Release nhả cả nhóm; Close không nhả, CR Closed không Release được | `containment-request` | `Release_frees_the_group_but_close_keeps_the_goods_held` |
| Một lô chỉ thuộc một buffer, gỡ trước khi gắn chỗ khác | `load-register` | `A_lot_belongs_to_one_buffer_and_must_be_detached_before_moving_to_another` |

## R4: solution NV1

Lớp Project, master data và WO theo ca ([ADR-057](adr/ADR-057-wo-theo-ca-va-suite-r4.md)). Test ở
`tests/Integration/Nvm.SolutionTests`, dùng đúng manifest của host.

| Hành vi | Nguồn | Test |
|---|---|---|
| Master data nạp bằng suite theo `masterDataOrder`, theo tên file; suite trong lịch sử thì bỏ qua | `master-data-suite` | `Master_data_loads_from_an_empty_database_and_the_next_deploy_skips_every_suite` |
| Đọc theo khóa, `IfThisSkipNext` trước bước tạo: chạy lại không tạo trùng | `master-data-suite` | `Rerunning_every_suite_reads_by_key_first_and_creates_no_duplicates` |
| `ExpectedSuccess = false` đạt với mọi lỗi; assert theo đường dẫn JSON; `#GET->…#` | `tester-tool-suite` | `Runner_passes_an_expected_failure_and_fails_on_a_wrong_assert` |
| Timer đầu ca: mỗi ca một WO mỗi DWO, WO ca trước Frozen, Complete sau `MinimumFreezeHours`; tên WO = tiền tố BoP + thời điểm | `work-order-theo-ca`, `ban-thiet-ke-va-ca` | `Three_days_of_shifts_open_one_work_order_per_default_freeze_the_previous_shift_and_complete_after_the_bop_hours` |
| Ngày đổi giờ với server UTC | plan R4 DoD | `Shift_windows_stay_contiguous_across_a_daylight_saving_change` |
| Chặn hai WO Active cùng máy cùng bước | `ban-thiet-ke-va-ca` | `Second_work_order_cannot_go_active_on_a_machine_that_already_runs_the_same_operation` |
| WO đã chạy không xóa được, về Edit thì xóa được | `tester-tool-suite` (suite 012) | `Started_work_order_cannot_be_deleted_until_it_is_back_in_edit` |
| Timer chạy dưới người gọi hệ thống, lệnh con không qua pre-check | `work-order-theo-ca` | `Shift_timer_runs_under_the_system_caller_without_the_team_pre_checks` |
| L06: role của Frozen → Aborted không có ở đâu, không ai hủy được | `role-huy-wo-frozen` | `L06_role_on_frozen_to_aborted_that_nobody_has_blocks_every_abort_until_an_existing_role_is_assigned` |
| L07: suite lỗi vào lịch sử, lần deploy sau bỏ qua | `master-data-suite` | `L07_failed_suite_written_to_history_is_skipped_by_the_next_deploy_and_only_counting_data_shows_it`, đối chứng `L07_control_history_written_only_on_success_lets_the_next_deploy_retry` |
| L08: timer theo giờ server, khung ca so theo UTC | `work-order-theo-ca` | `L08_shift_timer_uses_server_time_while_the_window_check_uses_utc` (hai biến thể) |
| L16: tham số từ cột CSV không có bị bỏ, suite xanh mà đổi sai material | `suite-doc-sai-cot-csv` | `L16_suite_reading_a_missing_csv_column_stays_green_and_changes_only_the_first_material` |

### Giả định của R4

| Chỗ chưa rõ | v2 làm | Cấu hình |
|---|---|---|
| Vì sao suite lỗi vẫn vào lịch sử | Mặc định chỉ ghi khi suite đạt; biến thể luôn ghi | `HistoryPolicy` |
| Múi giờ server thật | Mặc định UTC; lệch giờ cố định cho lab L08 | key `Nv.ServerUtcOffsetMinutes` |
| Điều kiện "mọi MTU đã chạy hết hoặc có NC" khi tự hoàn tất | Chưa kiểm: chưa có tin từ xưởng (R5) | — |
| Mã lỗi khi thiếu quyền gọi command | HTTP 403, `6403` | — |
| WO New được kích hoạt khi nào | Khi giờ hiện tại nằm trong khung dự kiến của nó | — |

## R5: tin từ xưởng

LineDB (Postgres) với bảng máy theo khu, bảng trung gian `relay`, hai đường chuyển tin; lớp Project xử lý tin bằng
`ApplyLineResults`. Script LineDB ở `src/Shopfloor/Nvm.Shopfloor/LineDb/Sql`. Test ở `tests/Integration/Nvm.SolutionTests`
(`LineMessageTests`, `LineLabTests`), SQL Server và Postgres thật.

| Hành vi | Nguồn | Test |
|---|---|---|
| Tin chỉ mang mã phía xưởng; MOM dịch bước, máy (hậu tố số line), nguyên liệu qua bảng đối chiếu của `MapApp` | `tin-tu-xuong` | `One_shift_on_one_line_records_exactly_the_scenario` |
| Chọn bước WO: serial đã thuộc WO → bước khớp bước và máy trong WO Active/New/Pause → WO dự phòng chỉ trong khung ca (UTC) | `tin-tu-xuong`, `serial-not-executed` | cùng test trên; `L09_…` |
| MTU sản phẩm `<material>_<serial>`, `Code` = serial; trừ nguyên liệu theo định mức khi tin không có lượng; không có lô thì MTU tạm `Auto_…`; lô chưa có thì tạo | `tin-tu-xuong` | `One_shift_on_one_line_records_exactly_the_scenario` |
| Response có `ExecutedSerialNumbers`, `NotExecutedOperations`; `Succeeded` chỉ nói lời gọi chạy xong | `serial-not-executed` | `L09_succeeded_response_still_lists_the_serial_as_not_executed` |
| Trạng thái dòng `NEW`/`SENT`/`DONE`/`NOTIFY_ERR`/`RETRY`/`FAILED`; mã lỗi ghép = −(loại xử lý ‖ mã gốc), loại 6 ghi operation, 5 hao hụt, 4 hàng lỗi | `tin-may-toi-mom` | `Unknown_machine_code_is_not_executed_with_its_composite_code` |
| Gửi lại theo cặp (trạng thái, mã lỗi): relay chỉ dòng có chính sách; poll mặc định thêm 1 lần sau 10 phút | `tin-may-ket-postgres` | `L10_rows_rejected_before_the_fix_stay_on_the_relay_path_until_set_back_to_new`, `L10_control_poll_path_…`, `Poll_path_recovers_by_itself_…` |
| Notify vượt 8000 byte → `NOTIFY_ERR`, không tự gửi lại | `tin-may-toi-mom` | `L11_payload_over_the_notify_limit_marks_rows_notify_err_and_they_are_never_resent` |
| Gửi lại không tạo MTU, không trừ nguyên liệu lần nữa (khóa `<serial>|<mã bước>`) | plan R5 DoD (K7) | `Rows_sent_again_do_not_create_or_consume_twice` |
| MOM dừng: relay để dòng ở SENT rồi gửi lại; poll để dòng ở NEW | plan R5 DoD | `Relay_path_drains_the_backlog_…`, `Poll_path_waits_while_mom_is_down_…` |
| Chỗ lưu trạng thái: cột theo môi trường hoặc bảng lịch riêng | `tin-may-ket-postgres` | `One_shift_…` (hai biến thể) |
| Role MOM chỉ gọi được hàm trung gian (K11) | plan R5 | `Mom_role_reaches_only_the_relay_functions` |
| Màn "Tin từ xưởng" (`/area/line/messages`): serial đã ghi, nguyên liệu đã trừ, ghi tay một tin và hiện nguyên response; trạng thái dòng LineDB không hiện vì web chỉ đi qua OData của MOM | `serial-not-executed` | `web/e2e/line-messages.spec.ts` (lab L09 trên giao diện) |
| Tải gấp đôi 60 phút trên đường poll: không mất dòng, tồn đọng không tăng, p95 dưới 120 s | plan R5 DoD | đo bằng `make line-load`, số ở [benchmarks](benchmarks.md#r5--tin-từ-xưởng-tải-gấp-đôi) |

### Giả định của R5

| Chỗ chưa rõ | v2 làm | Cấu hình |
|---|---|---|
| Đường chuyển tin đang chạy | Cả hai; mặc định relay | key `Nv.LineTransfer` (`relay`/`poll`); bước deploy `--enable-line-polling` |
| Chỗ lưu trạng thái dòng | Cả hai qua view `relay.message_status` | `NVM_LINEDB_STATE` (`table`/`columns`), `NVM_ENVIRONMENT` |
| Bảng chính sách trên môi trường thật | Dòng lỗi chung −6999, −5999, −4999; thêm (SENT, 0) để dòng mất notify được gửi lại | dữ liệu `relay.retry_policy` |
| Số đã làm của WO tăng ở bước nào | Bước cuối của process (mọi process NV1 một bước) | — |
| Serial đã thuộc WO nhưng WO đó không còn mở | Vẫn dùng WO đó | — |
| Lô trong tin thiếu lượng | Nâng lượng lô (`EditMtu`) rồi trừ, ghi trace Warning | — |
| Kết quả ghi về LineDB trước commit của MOM | Đúng thứ tự đó; commit hỏng thì dòng DONE mà MOM không có dữ liệu | — |
| Tin hao hụt | Tách riêng, R5 trả `FAILED` mã −5900; xử lý ở R6 | — |
| Tin KO | Ghi như OK, serial vào `PendingNonConformance`; NC ở R6 | — |

### Giả định của lớp Product

| Chỗ chưa rõ | Lớp Product làm |
|---|---|
| Trừ nguyên liệu khi lô không đủ | Từ chối (`7303`); nguồn chỉ cho biết code của team nâng số lượng lô trước khi trừ |
| Thả danh sách MTU (`ReleaseMtus`) khi MTU còn NC OPEN hoặc đang bị khoanh | Từ chối cả lệnh (`7504`) |
| Release containment với MTU còn NC OPEN | MTU giữ OnHold, chỉ bỏ cờ In Containment |
| Định dạng số CR | `ContainmentRequest_` + 6 chữ số, tiếp nối số CR đang sống |
| Whitelist của buffer | Buffer không có dòng whitelist thì nhận mọi material |
| Yêu cầu vận chuyển | Created → Released → InTransit (mỗi chặng chuyển MTU) → Completed (MTU tới đích); R10 đọc nguồn kỹ hơn |
| Cấp của cây thiết bị | Tám cấp Enterprise, Site, Plant, Area, Subarea, WorkCenter, WorkUnit, Machine; con chỉ cần sâu hơn cha |
| Trạng thái MTU | Hằng số, không phải dữ liệu (ADR-056) |

## Giả định chưa xác minh trên hệ thật

Dựng theo scope §7: chỗ nào chưa quan sát được thì ghi ra đây, và nếu có hai cách hợp lý thì cả hai
chọn bằng cấu hình.

| Chỗ chưa rõ | Foundation làm | Cấu hình |
|---|---|---|
| Command con lỗi mà handler cha không kiểm `Succeeded`, vẫn trả thành công | Mặc định commit theo response của command gốc | `ChildFailureDoomsTransaction` |
| Commit hỏng sau khi handler xong | Mặc định response báo lỗi; biến thể giữ `Succeeded` của handler cho lab L02 | `CommitFailureReporting` |
| Pre-check có chạy khi command được gọi bằng `CallCommand` trong handler không | Không: pre-check bật theo app, lời gọi trong handler không qua app | — |
| Pre-check có chạy cho lời gọi con của composite không | Có: composite thuộc một app, lời gọi con mang app đó | — |
| Handler ném exception không bắt | Response lỗi dạng handler, `ErrorCode` = `HResult` của exception, lý do ở trace | — |
| Facet một dòng mỗi (entity, facet) hay một dòng mỗi entity | Một dòng mỗi (entity, facet), cột `FacetType` | — |
| Subscriber lỗi có được thử lại không | Không: ghi trace, rollback, xác nhận message | — |
| Subscription theo plant có hiệu lực ngay hay sau khởi động lại | Sau khi worker khởi động lại | — |
| Mã lỗi khi thiếu token (khác hết hạn) | `Unauthorized`; chỉ hết hạn mới là `6401` | — |
